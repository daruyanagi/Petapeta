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
    // 保留内容を読み取った時点のクリップボード世代。書き戻し直前に一致を
    // 確認し、処理中に発生した新しいコピーを上書きしない(#14)
    private uint _pendingSequence;
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
        if (isExplorer)
        {
            if (IsEnabled && TextEnabled && _pendingText is not null && !_textAugmented)
            {
                _ = RunSafeAsync(AugmentTextAsync);
            }
        }
        else if (_textAugmented)
        {
            // 復元は自分が書き換えたクリップボードの後始末なので、
            // 監視の一時停止中でも実行する。止めてしまうと HDROP が残り、
            // Web エディター等でファイル貼り付けが優先される問題が再発する(#15)
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
        // Chromium 等のクリップボード書き込みは非アトミックで、変化通知の
        // 時点では形式が出揃っていないことがある。対象形式が見つからない
        // 場合は少し待ち、新しいビューを取り直して再判定する(#10)
        DataPackageView? view;
        bool rawBitmap;
        bool rawText;
        for (var attempt = 0; ; attempt++)
        {
            view = await GetContentWithRetryAsync();
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

            rawBitmap = view.Contains(StandardDataFormats.Bitmap);
            rawText = view.Contains(StandardDataFormats.Text);
            if (rawBitmap || rawText || attempt >= 2)
            {
                break;
            }
            await Task.Delay(200);
        }

        var hasBitmap = rawBitmap && ImageEnabled;
        var hasText = rawText && TextEnabled;

        // この時点のクリップボード世代。以降の書き戻しはこの世代が
        // 変わっていないことを条件にする(#14)
        var sequence = GetClipboardSequenceNumber();

        // コピー元の特定は自分で再セットする前に行う(再セット後は所有者が自分になる)
        var sourceApp = GetClipboardOwnerProcessName();

        if (hasBitmap)
        {
            ClearPendingText();
            await AugmentImageAsync(view, sourceApp, sequence);
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
        _pendingSequence = sequence;
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
    private async Task AugmentImageAsync(DataPackageView view, string? sourceApp, uint sequence)
    {
        // コピー元の書き込みが未完のストリームを読むと、行単位のゴミや
        // WINCODEC_ERR_UNEXPECTEDSIZE (0x88982F72) になる。全量をバッファに
        // 読み、二重読みで安定を確認してからデコードする(#25)
        var data = await ReadStableBitmapBytesAsync(view);
        if (data is null)
        {
            return;
        }

        var path = CreateUniqueStagingPath(".png");
        try
        {
            await SavePngAsync(data, path);
        }
        catch
        {
            // 失敗した予約ファイル(0バイト残骸)を残さない(#25)
            try { File.Delete(path); } catch { }
            throw;
        }
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

        // エンコード中に新しいコピーが発生していたら書き戻さない(#14)
        if (GetClipboardSequenceNumber() != sequence)
        {
            try { File.Delete(path); } catch { }
            Emit(R.Get("LogStaleClipboardSkip"));
            return;
        }

        if (await SetContentWithRetryAsync(package, CreateOptions()))
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

        // ダウンロード等の最中に新しいコピーが発生していたら書き戻さない。
        // 新しいコピーのイベントはゲート待ちで後続処理される(#14)
        if (GetClipboardSequenceNumber() != _pendingSequence)
        {
            Emit(R.Get("LogStaleClipboardSkip"));
            return;
        }

        if (await SetContentWithRetryAsync(BuildTextPackage(file), CreateOptions()))
        {
            _textAugmented = true;
            // 自分の書き込みで世代が進むため取り直す(再追加・解除を壊さない)
            _pendingSequence = GetClipboardSequenceNumber();
            Emit(FormatFileAdded(file.Name, _pendingSourceApp));
            SoundService.PlayFeedback();
        }
        else
        {
            Emit(R.Get("LogSetContentFailed"));
        }
    }

    /// <summary>CF_HDROP を外し、テキストのみのクリップボードへ戻す。</summary>
    private async Task RestoreTextAsync()
    {
        _textAugmented = false;

        if (_pendingText is null)
        {
            return;
        }

        // 他のアプリが既にクリップボードを書き換えていたら触らない
        var view = await GetContentWithRetryAsync();
        if (view is null || !view.Contains(MarkerFormat) || !view.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        if (await SetContentWithRetryAsync(BuildTextPackage(file: null), CreateOptions()))
        {
            // 自分の書き込みで世代が進むため取り直す(#14)
            _pendingSequence = GetClipboardSequenceNumber();
            Emit(R.Get("LogHdropRemoved"));
        }
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
        _pendingSequence = 0;
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
    private static extern uint GetClipboardSequenceNumber();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    // リダイレクト先を自前で再検証するため自動リダイレクトは無効にする(#18)
    private static readonly System.Net.Http.HttpClient Http = new(
        new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private const int MaxRedirects = 3;

    /// <summary>
    /// ダウンロードを許可するホストか。ループバック・プライベート・
    /// リンクローカル宛(社内機器等)への自動アクセスを防ぐ(#18)。
    /// ホスト名は DNS 解決して全アドレスを確認する。
    /// </summary>
    private static async Task<bool> IsAllowedHostAsync(Uri uri)
    {
        try
        {
            System.Net.IPAddress[] addresses;
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            {
                addresses = new[] { System.Net.IPAddress.Parse(uri.IdnHost) };
            }
            else
            {
                addresses = await System.Net.Dns.GetHostAddressesAsync(uri.IdnHost);
            }
            return addresses.Length > 0 && !addresses.Any(IsBlockedAddress);
        }
        catch
        {
            // 解決できないホストは拒否(どのみち接続も失敗する)
            return false;
        }
    }

    private static bool IsBlockedAddress(System.Net.IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (System.Net.IPAddress.IsLoopback(ip))
        {
            return true;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0
                || b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal
                || ip.Equals(System.Net.IPAddress.IPv6Any);
        }
        // 未知のアドレスファミリは拒否
        return true;
    }

    /// <summary>
    /// リダイレクトを最大 MaxRedirects 回まで手動で追跡する。各リダイレクト先も
    /// スキーム・ホストの検証を通す(#18)。最終応答を返す(失敗時 null)。
    /// </summary>
    private static async Task<System.Net.Http.HttpResponseMessage?> FetchWithValidatedRedirectsAsync(Uri uri)
    {
        var current = uri;
        for (var redirects = 0; ; redirects++)
        {
            var response = await Http.GetAsync(current, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            var status = (int)response.StatusCode;
            if (status < 300 || status >= 400)
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (redirects >= MaxRedirects || location is null)
            {
                return null;
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            if ((current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps)
                || !await IsAllowedHostAsync(current))
            {
                return null;
            }
        }
    }

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

        // プライベート/ループバック宛の URL は拒否(#18)
        if (!await IsAllowedHostAsync(uri))
        {
            return null;
        }

        string? path = null;
        try
        {
            using var response = await FetchWithValidatedRedirectsAsync(uri);
            if (response is null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            // SVG はスクリプトを含み得るため対象外(#18)
            var extension = response.Content.Headers.ContentType?.MediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/bmp" => ".bmp",
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

    /// <summary>
    /// クリップボードのビットマップを全量読み、直後の再読で内容が一致する
    /// (=コピー元の書き込みが完了している)ことを確認して返す(#25)。
    /// 不安定・読み取り失敗時は待ってから新しいビューで取り直す。
    /// </summary>
    private async Task<byte[]?> ReadStableBitmapBytesAsync(DataPackageView view)
    {
        // サイズ上限の事前チェック(従来のログを維持)
        try
        {
            var reference = await view.GetBitmapAsync();
            using var probe = await reference.OpenReadAsync();
            if (probe.Size > MaxImageBytes)
            {
                Emit(R.F("LogImageTooLarge", probe.Size / 1024 / 1024));
                return null;
            }
        }
        catch
        {
            // 読み取り失敗は下のループでリトライされる
        }

        byte[]? previous = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            byte[]? current = null;
            try
            {
                current = await ReadBitmapBytesOnceAsync(view);
            }
            catch
            {
                // WINCODEC_ERR_UNEXPECTEDSIZE (0x88982F72) 等 → 取り直しへ
            }

            if (current is not null && previous is not null && BuffersLookEqual(previous, current))
            {
                return current;  // 2回連続で一致 → 安定(通常は待ちなしの2回で確定)
            }
            previous = current;

            if (attempt >= 1)
            {
                // 不安定 → コピー元の書き込み完了を待って新しいビューで取り直す
                await Task.Delay(200);
                var fresh = await GetContentWithRetryAsync();
                if (fresh is null || fresh.Contains(MarkerFormat)
                    || !fresh.Contains(StandardDataFormats.Bitmap))
                {
                    return null;  // 内容が変わった/読めない → このコピーは打ち切り
                }
                view = fresh;
            }
        }

        Emit(R.Get("LogImageReadUnstable"));
        return null;
    }

    private static async Task<byte[]?> ReadBitmapBytesOnceAsync(DataPackageView view)
    {
        var reference = await view.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();
        if (source.Size == 0 || source.Size > MaxImageBytes)
        {
            return null;
        }

        using var stream = source.AsStreamForRead();
        using var memory = new MemoryStream((int)source.Size);
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static bool BuffersLookEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        var tail = Math.Min(256, a.Length);
        return a.AsSpan(a.Length - tail).SequenceEqual(b.AsSpan(b.Length - tail));
    }

    private static async Task SavePngAsync(byte[] data, string path)
    {
        // 完全性を確認済みのバッファからデコードする(#25)
        using var memory = new InMemoryRandomAccessStream();
        await memory.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(data));
        memory.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(memory);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using var fileStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var dest = fileStream.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, dest);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }

    // 注意: Clipboard API は UI(STA)スレッドを要するため ConfigureAwait(false) は
    // 使わない(await 後も UI コンテキストで継続させる)。同期版の
    // Task.Delay().Wait() は UI スレッドを最大約 500ms ブロックしていた(#16)
    private static async Task<DataPackageView?> GetContentWithRetryAsync()
    {
        for (var i = 0; i < MaxRetries; i++)
        {
            try
            {
                return Clipboard.GetContent();
            }
            catch
            {
                await Task.Delay(RetryDelayMs);
            }
        }
        return null;
    }

    private static async Task<bool> SetContentWithRetryAsync(DataPackage package, ClipboardContentOptions options)
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
            await Task.Delay(RetryDelayMs);
        }
        return false;
    }
}
