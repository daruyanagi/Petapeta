using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Petapeta.Services;

/// <summary>
/// クリップボードを監視してファイル貼り付けを可能にする。
///
/// 画像: コピーされた時点で PNG を書き出し CF_HDROP(StorageItems)を追加する。
/// テキスト: コピー時点では何もせず内容を保留し、エクスプローラーが最前面に
/// なった瞬間に .txt 化して CF_HDROP を追加、他アプリへ切り替わったら解除する。
/// (Web エディター等が HDROP を優先してテキスト貼り付けを乗っ取る問題の回避)
/// </summary>
public sealed class ClipboardMonitorService
{
    // 自分の書き換えを検出するためのマーカー形式(ループ防止)
    private const string MarkerFormat = "Petapeta.Processed";
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

    // テキストの保留状態。ContentChanged とフォアグラウンド通知は
    // どちらも UI スレッドで届くため、ロックは不要
    private string? _pendingText;
    private string? _pendingHtml;
    private string? _pendingRtf;
    private string? _pendingFilePath;
    private string? _pendingSourceApp;
    private bool _textAugmented;

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

    /// <summary>最前面がエクスプローラーかどうかの変化を受け取る(ForegroundWatcher から)。</summary>
    public void OnExplorerForegroundChanged(bool isExplorer)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (isExplorer)
        {
            if (TextEnabled && _pendingText is not null && !_textAugmented)
            {
                _ = RunSafeAsync(AugmentTextAsync);
            }
        }
        else if (_textAugmented)
        {
            _ = RunSafeAsync(RestoreTextAsync);
        }
    }

    // クリップボード更新イベントは短時間に複数回飛ぶことがある(Chromium の
    // 遅延フラッシュ等)。処理を直列化して二重ファイル化・ファイル名競合を防ぐ(#8)
    private readonly SemaphoreSlim _processGate = new(1, 1);

    private async Task RunSafeAsync(Func<Task> action)
    {
        await _processGate.WaitAsync();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Emit(R.F("LogError", Describe(ex)));
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <summary>
    /// 例外の型と HRESULT を含む診断文字列。COM/WinRT 例外は Message が
    /// 空のことがあり「エラー: 」だけでは調査不能なため(#03:49:52 問題)。
    /// </summary>
    private static string Describe(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message)
            ? $"{ex.GetType().Name} (0x{ex.HResult:X8})"
            : $"{ex.Message} — {ex.GetType().Name} (0x{ex.HResult:X8})";

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
        LogFileService.Append(line);
        Log?.Invoke(line);
    }

    private async void OnContentChanged(object? sender, object? e)
    {
        if (!IsEnabled)
        {
            return;
        }

        // 直列化(#8)。2発目以降のイベントは処理後の再読取でマーカーを
        // 検知して no-op になる
        await RunSafeAsync(ProcessAsync);
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
            ClearPendingText();
            Emit(R.Get("LogFileCopyDetected"));
            return;
        }

        var rawBitmap = view.Contains(StandardDataFormats.Bitmap);
        var rawText = view.Contains(StandardDataFormats.Text);
        var hasBitmap = rawBitmap && ImageEnabled;
        var hasText = rawText && TextEnabled;

        // コピー元の特定は自分で再セットする前に行う(再セット後は所有者が自分になる)
        var sourceApp = GetClipboardOwnerProcessName();

        if (hasBitmap)
        {
            ClearPendingText();
            await AugmentImageAsync(view, sourceApp);
            return;
        }

        if (!hasText)
        {
            ClearPendingText();
            // 形式的に対象外だったときだけ記録する(設定オフでのスルーは無ログ)。
            // 「イベント未着」と「形式判定でスルー」を事後に切り分けるため(#9)
            if (!rawBitmap && !rawText)
            {
                var formats = string.Join(", ", view.AvailableFormats.Take(8));
                Emit(R.F("LogIgnoredFormats", formats.Length == 0 ? "-" : formats));
            }
            return;
        }

        var text = await view.GetTextAsync();
        if (text.Length > MaxTextChars)
        {
            ClearPendingText();
            Emit(R.Get("LogTextTooLarge"));
            return;
        }

        // ここではクリップボードを書き換えず保留のみ。エクスプローラーが
        // 前面になったとき(または既に前面のとき)にファイル化する
        _pendingText = text;
        _pendingHtml = null;
        _pendingRtf = null;
        _pendingFilePath = null;
        _pendingSourceApp = sourceApp;
        _textAugmented = false;

        if (view.Contains(StandardDataFormats.Html))
        {
            try { _pendingHtml = await view.GetHtmlFormatAsync(); } catch { }
        }
        if (view.Contains(StandardDataFormats.Rtf))
        {
            try { _pendingRtf = await view.GetRtfAsync(); } catch { }
        }

        // 保留したことを記録する。コピー直後に「無反応」に見えるのを防ぎ、
        // イベント自体が届いたことの証跡にもなる(#9)
        Emit(sourceApp is null
            ? R.Get("LogTextPending")
            : R.F("LogTextPendingFrom", sourceApp));

        if (ForegroundWatcher.IsExplorerForeground())
        {
            await AugmentTextAsync();
        }
    }

    /// <summary>画像を PNG として書き出し、CF_HDROP を追加して再セットする。</summary>
    private async Task AugmentImageAsync(DataPackageView view, string? sourceApp)
    {
        var reference = await view.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();
        if (source.Size > MaxImageBytes)
        {
            Emit(R.F("LogImageTooLarge", source.Size / 1024 / 1024));
            return;
        }

        var path = CreateUniqueStagingPath(".png");
        await SavePngAsync(source, path);
        NoteStagingFileCreated();
        var file = await StorageFile.GetFileFromPathAsync(path);

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));

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

        if (SetContentWithRetry(package, CreateOptions()))
        {
            Emit(FormatFileAdded(file.Name, sourceApp));
            SoundService.PlayFeedback();
        }
        else
        {
            Emit(R.Get("LogSetContentFailed"));
        }
    }

    /// <summary>保留中のテキストを .txt 化し、CF_HDROP 付きで再セットする。</summary>
    private async Task AugmentTextAsync()
    {
        if (_pendingText is null)
        {
            return;
        }

        if (_pendingFilePath is null)
        {
            // テキストが画像 URL なら画像として取得(#7)。だめなら .txt へ
            var path = await TryDownloadImageUrlAsync(_pendingText);
            if (path is null)
            {
                path = CreateUniqueStagingPath(".txt");
                await File.WriteAllTextAsync(path, _pendingText);
            }
            _pendingFilePath = path;
            NoteStagingFileCreated();
        }

        var file = await StorageFile.GetFileFromPathAsync(_pendingFilePath);
        if (SetContentWithRetry(BuildTextPackage(file), CreateOptions()))
        {
            _textAugmented = true;
            Emit(FormatFileAdded(file.Name, _pendingSourceApp));
            SoundService.PlayFeedback();
        }
        else
        {
            Emit(R.Get("LogSetContentFailed"));
        }
    }

    /// <summary>CF_HDROP を外し、テキストのみのクリップボードへ戻す。</summary>
    private Task RestoreTextAsync()
    {
        _textAugmented = false;

        if (_pendingText is null)
        {
            return Task.CompletedTask;
        }

        // 他のアプリが既にクリップボードを書き換えていたら触らない
        var view = GetContentWithRetry();
        if (view is null || !view.Contains(MarkerFormat) || !view.Contains(StandardDataFormats.StorageItems))
        {
            return Task.CompletedTask;
        }

        if (SetContentWithRetry(BuildTextPackage(file: null), CreateOptions()))
        {
            Emit(R.Get("LogHdropRemoved"));
        }

        return Task.CompletedTask;
    }

    private DataPackage BuildTextPackage(StorageFile? file)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(_pendingText!);
        if (_pendingHtml is not null)
        {
            package.SetHtmlFormat(_pendingHtml);
        }
        if (_pendingRtf is not null)
        {
            package.SetRtf(_pendingRtf);
        }
        if (file is not null)
        {
            package.SetStorageItems(new[] { file }, readOnly: false);
        }
        package.SetData(MarkerFormat, "1");
        return package;
    }

    private void ClearPendingText()
    {
        _pendingText = null;
        _pendingHtml = null;
        _pendingRtf = null;
        _pendingFilePath = null;
        _pendingSourceApp = null;
        _textAugmented = false;
    }

    private static string FormatFileAdded(string fileName, string? sourceApp) =>
        string.IsNullOrEmpty(sourceApp)
            ? R.F("LogFileAdded", fileName)
            : R.F("LogFileAddedFrom", fileName, sourceApp);

    /// <summary>
    /// クリップボード所有者(コピー元アプリ)のプロセス名。取得できなければ null。
    /// 内容は書かずアプリ名だけをログに残す。
    /// </summary>
    private static string? GetClipboardOwnerProcessName()
    {
        try
        {
            var hwnd = GetClipboardOwner();
            if (hwnd == 0)
            {
                return null;
            }
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return null;
            }
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    private static readonly System.Net.Http.HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// テキストが画像を指す URL なら画像をダウンロードしてステージングし、
    /// パスを返す(#7)。URL でない・画像でない・失敗時は null(.txt へフォールバック)。
    /// 拡張子ではなく Content-Type で判定する。
    /// </summary>
    private async Task<string?> TryDownloadImageUrlAsync(string text)
    {
        if (!SettingsService.UrlImageEnabled)
        {
            return null;
        }

        var candidate = text.Trim();
        if (candidate.Length > 2048
            || candidate.IndexOfAny(new[] { '\r', '\n', ' ' }) >= 0
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string? path = null;
        try
        {
            using var response = await Http.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var extension = response.Content.Headers.ContentType?.MediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/bmp" => ".bmp",
                "image/svg+xml" => ".svg",
                "image/avif" => ".avif",
                _ => null,
            };
            if (extension is null)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > (long)MaxImageBytes)
            {
                return null;
            }

            path = CreateUniqueStagingPath(extension);
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var dest = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                // Content-Length 詐称・欠落に備えてストリーミングで上限を確認する
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > (long)MaxImageBytes)
                    {
                        throw new InvalidOperationException("サイズ上限超過");
                    }
                    await dest.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            Emit(R.F("LogUrlImageDownloaded", Path.GetFileName(path)));
            return path;
        }
        catch
        {
            // 失敗時は途中生成物を消して .txt フォールバックへ
            if (path is not null)
            {
                try { File.Delete(path); } catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// ステージング内で重複しないファイルパスをアトミックに確保する。
    /// 存在チェック方式は並走時に同じパスを返し得るため、CreateNew で
    /// 実際に確保できるまで連番を進める(#8)。
    /// </summary>
    private static string CreateUniqueStagingPath(string extension)
    {
        var dir = AppPaths.EnsureStaging();
        var baseName = $"Clipboard {DateTime.Now:yyyy-MM-dd HHmmss}";
        for (var i = 1; ; i++)
        {
            var path = Path.Combine(dir, i == 1 ? baseName + extension : $"{baseName} ({i}){extension}");
            try
            {
                using (new FileStream(path, FileMode.CreateNew)) { }
                return path;
            }
            catch (IOException)
            {
                // 既に存在 → 次の連番へ
            }
        }
    }

    // Win+V 履歴には元のコピーが既に載っているので、書き換え分は履歴に入れない
    private static ClipboardContentOptions CreateOptions() => new()
    {
        IsAllowedInHistory = false,
        IsRoamable = false,
    };

    // ローワーターマーク方式のクリーンアップ(#4):
    // 書き出しのたびに走らせず、件数がハイウォーターマーク(上限+Slack)に
    // 達したとき・24時間経過・起動時のみ実行し、超過分をまとめて削除する。
    private const int CleanupSlack = 20;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    private int _stagingCount = -1;  // -1 = 未初期化(起動時のクリーンアップで確定)
    private DateTime _lastCleanup = DateTime.MinValue;

    /// <summary>ステージングにファイルを1件書き出したときに呼ぶ。必要ならクリーンアップを予約する。</summary>
    private void NoteStagingFileCreated()
    {
        var count = Interlocked.Increment(ref _stagingCount);
        var highWatermark = SettingsService.MaxFiles + CleanupSlack;
        if (count >= highWatermark || DateTime.Now - _lastCleanup > CleanupInterval)
        {
            _ = Task.Run(CleanupStaging);
        }
    }

    /// <summary>
    /// 保持期間・件数(ローワーターマーク=最大保持件数)を超えたファイルを
    /// まとめて削除する。起動時・ウォーターマーク到達時・手動実行時に呼ばれる。
    /// </summary>
    private void CleanupStaging()
    {
        try
        {
            _lastCleanup = DateTime.Now;

            var path = AppPaths.StagingPath;
            if (!Directory.Exists(path))
            {
                Interlocked.Exchange(ref _stagingCount, 0);
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

            Interlocked.Exchange(ref _stagingCount, files.Count - deleted);

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

    private static async Task SavePngAsync(IRandomAccessStreamWithContentType source, string path)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using var fileStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var dest = fileStream.AsRandomAccessStream();
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
