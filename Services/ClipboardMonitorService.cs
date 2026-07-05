using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Petapeta.Services;

/// <summary>
/// クリップボードを監視し、画像・テキストがコピーされたとき
/// 実体ファイルを書き出して CF_HDROP(StorageItems)を追加する。
/// 元の形式(テキスト/HTML/RTF/ビットマップ)は可能な範囲で引き継ぐ。
/// </summary>
public sealed class ClipboardMonitorService
{
    // 自分の書き換えを検出するためのマーカー形式(ループ防止)
    private const string MarkerFormat = "Petapeta.Processed";
    private const string StagingFolderName = "Staging";
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 100;
    private const ulong MaxImageBytes = 50 * 1024 * 1024;
    private const int MaxTextChars = 10 * 1024 * 1024;

    private bool _isEnabled = true;

    /// <summary>監視の有効/無効。UI から複数箇所で切り替わるため単一の真実として扱う。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }
            _isEnabled = value;
            EnabledChanged?.Invoke(value);
        }
    }

    public bool ImageEnabled { get; set; } = SettingsService.ImageEnabled;
    public bool TextEnabled { get; set; } = SettingsService.TextEnabled;

    public event Action<string>? Log;

    /// <summary>IsEnabled が変化したときに発火(値が実際に変わったときのみ)。</summary>
    public event Action<bool>? EnabledChanged;

    private readonly object _logLock = new();
    private readonly List<string> _backlog = new();
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Clipboard.ContentChanged += OnContentChanged;
        Emit(R.Get("LogMonitoringStarted"));
        _ = Task.Run(CleanupStaging);
    }

    /// <summary>設定変更などをホームのログに流すための入口。</summary>
    public void Note(string message) => Emit(message);

    /// <summary>クリーンアップを即時実行する(バックグラウンド)。</summary>
    public void RunCleanup() => _ = Task.Run(CleanupStaging);

    /// <summary>UI 接続前に発生したログを取得する(ウィンドウ非表示の自動起動対応)。</summary>
    public string[] GetBacklog()
    {
        lock (_logLock)
        {
            return _backlog.ToArray();
        }
    }

    private void Emit(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (_logLock)
        {
            _backlog.Add(line);
            if (_backlog.Count > 100)
            {
                _backlog.RemoveAt(0);
            }
        }
        Log?.Invoke(line);
    }

    private async void OnContentChanged(object? sender, object? e)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            await ProcessAsync();
        }
        catch (Exception ex)
        {
            Emit(R.F("LogError", ex.Message));
        }
    }

    private async Task ProcessAsync()
    {
        var view = GetContentWithRetry();
        if (view is null)
        {
            Emit(R.Get("LogClipboardBusy"));
            return;
        }

        if (view.Contains(MarkerFormat))
        {
            // 自分が書き換えた内容なので何もしない
            return;
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            Emit(R.Get("LogFileCopyDetected"));
            return;
        }

        var hasBitmap = view.Contains(StandardDataFormats.Bitmap) && ImageEnabled;
        var hasText = view.Contains(StandardDataFormats.Text) && TextEnabled;

        if (!hasBitmap && !hasText)
        {
            return;
        }

        var staging = await GetStagingFolderAsync();
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        StorageFile file;

        if (hasBitmap)
        {
            var reference = await view.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            if (source.Size > MaxImageBytes)
            {
                Emit(R.F("LogImageTooLarge", source.Size / 1024 / 1024));
                return;
            }

            file = await staging.CreateFileAsync(
                $"Clipboard {timestamp}.png", CreationCollisionOption.GenerateUniqueName);
            await SavePngAsync(source, file);
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }
        else
        {
            var text = await view.GetTextAsync();
            if (text.Length > MaxTextChars)
            {
                Emit(R.Get("LogTextTooLarge"));
                return;
            }

            file = await staging.CreateFileAsync(
                $"Clipboard {timestamp}.txt", CreationCollisionOption.GenerateUniqueName);
            await FileIO.WriteTextAsync(file, text);
        }

        // 元の形式を可能な範囲で引き継ぐ(取得に失敗した形式は諦める)
        if (view.Contains(StandardDataFormats.Text))
        {
            try { package.SetText(await view.GetTextAsync()); } catch { }
        }
        if (view.Contains(StandardDataFormats.Html))
        {
            try { package.SetHtmlFormat(await view.GetHtmlFormatAsync()); } catch { }
        }
        if (view.Contains(StandardDataFormats.Rtf))
        {
            try { package.SetRtf(await view.GetRtfAsync()); } catch { }
        }

        package.SetStorageItems(new[] { file }, readOnly: false);
        package.SetData(MarkerFormat, "1");

        // Win+V 履歴には元のコピーが既に載っているので、書き換え分は履歴に入れない
        var options = new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false,
        };

        if (SetContentWithRetry(package, options))
        {
            Emit(R.F("LogFileAdded", file.Name));
            SoundService.PlayFeedback();
            _ = Task.Run(CleanupStaging);
        }
        else
        {
            Emit(R.Get("LogSetContentFailed"));
        }
    }

    /// <summary>保持期間・件数を超えた古いステージングファイルを削除する。</summary>
    private void CleanupStaging()
    {
        try
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, StagingFolderName);
            if (!Directory.Exists(path))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-SettingsService.RetentionDays);
            var maxFiles = SettingsService.MaxFiles;
            var files = new DirectoryInfo(path).GetFiles()
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            var deleted = 0;
            for (var i = 0; i < files.Count; i++)
            {
                if (i >= maxFiles || files[i].CreationTimeUtc < cutoff)
                {
                    try
                    {
                        files[i].Delete();
                        deleted++;
                    }
                    catch
                    {
                        // 使用中などで消せないファイルは次回に持ち越し
                    }
                }
            }

            if (deleted > 0)
            {
                Emit(R.F("LogCleanupDeleted", deleted));
            }
        }
        catch (Exception ex)
        {
            Emit(R.F("LogCleanupFailed", ex.Message));
        }
    }

    private static async Task<StorageFolder> GetStagingFolderAsync() =>
        await ApplicationData.Current.LocalFolder
            .CreateFolderAsync(StagingFolderName, CreationCollisionOption.OpenIfExists);

    private static async Task SavePngAsync(IRandomAccessStreamWithContentType source, StorageFile file)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using var dest = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, dest);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }

    private static DataPackageView? GetContentWithRetry()
    {
        for (var i = 0; i < MaxRetries; i++)
        {
            try
            {
                return Clipboard.GetContent();
            }
            catch
            {
                Task.Delay(RetryDelayMs).Wait();
            }
        }
        return null;
    }

    private static bool SetContentWithRetry(DataPackage package, ClipboardContentOptions options)
    {
        for (var i = 0; i < MaxRetries; i++)
        {
            try
            {
                if (Clipboard.SetContentWithOptions(package, options))
                {
                    return true;
                }
            }
            catch
            {
                // 使用中の可能性 — リトライへ
            }
            Task.Delay(RetryDelayMs).Wait();
        }
        return false;
    }
}
