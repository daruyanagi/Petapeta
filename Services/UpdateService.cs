using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Petapeta.Services;

/// <summary>
/// 更新チェックの中枢(#12)。チャネル(ZIP / winget / MSIX)ごとに
/// 「どこに来た更新なら意味があるか」を分けて照会する。
///
/// - MSIX 版: 何もしない(Store / Windows Update に任せる)
/// - winget 版: winget に来ているバージョンだけを見る。GitHub へは
///   フォールバックしない。GitHub には出ていても winget 側の審査が
///   終わっていない版を「更新あり」と言っても、winget では入手できない
/// - ZIP 版: GitHub Releases の最新を見る
/// </summary>
internal static class UpdateService
{
    internal const string WingetId = "daruyanagi.Petapeta";
    internal const string LatestReleaseApi = "https://api.github.com/repos/daruyanagi/Petapeta/releases/latest";
    internal const string ReleasesPageUrl = "https://github.com/daruyanagi/Petapeta/releases/latest";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(2);

    // 更新チェック用の 15 秒では ZIP 本体を取り切れない。
    // ダウンロードの打ち切りは CancellationToken(取り消しボタン)で行う。
    private static readonly HttpClient CheckHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static readonly HttpClient DownloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>更新の有無が変わったとき(UI バッジの更新用。UI スレッドとは限らない)。</summary>
    internal static event Action? AvailabilityChanged;

    /// <summary>実行中のバージョン(3 桁)。</summary>
    internal static Version CurrentVersion { get; } = Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>更新があるときそのタグ(例 "v1.0.5")。無ければ null。</summary>
    internal static string? AvailableTag
    {
        get
        {
            var cached = SettingsService.CachedLatestVersion;
            return cached is not null
                && Version.TryParse(cached.TrimStart('v', 'V'), out var v)
                && v > CurrentVersion
                ? cached : null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// 起動 5 秒後から 24 時間ごと(失敗時は 2 時間後に再試行)に確認する。
    /// トレイ常駐で起動しっぱなしになるため、起動時 1 回では数日間気づけない。
    /// </summary>
    internal static async Task RunBackgroundLoopAsync()
    {
        if (PackageContext.IsPackaged) return;   // Store / Windows Update に任せる

        await Task.Delay(TimeSpan.FromSeconds(5));
        while (true)
        {
            var ok = true;
            if (SettingsService.UpdateCheckEnabled)
            {
                ok = await CheckOnceAsync() is not null;
            }
            await Task.Delay(ok ? CheckInterval : RetryInterval);
        }
    }

    /// <summary>
    /// 最新バージョンを 1 回確認して状態へ反映する。
    /// 取得できなかったときは null(既存の表示は変えない)。
    /// </summary>
    internal static async Task<Version?> CheckOnceAsync()
    {
        try
        {
            var latest = await FetchLatestVersionAsync();
            if (latest is null)
            {
                Trace("UpdateCheck: 最新バージョンを取得できなかった");
                return null;
            }

            var wasAvailable = AvailableTag is not null;
            SettingsService.CachedLatestVersion = latest > CurrentVersion ? $"v{latest.ToString(3)}" : null;
            SettingsService.LastUpdateCheck = DateTimeOffset.Now;
            Trace($"UpdateCheck: current=v{CurrentVersion.ToString(3)} latest=v{latest.ToString(3)} "
                + $"available={AvailableTag is not null}");

            if (!wasAvailable && AvailableTag is { } tag)
            {
                App.Monitor.Note(R.F("LogUpdateAvailable", tag));
            }
            AvailabilityChanged?.Invoke();
            return latest;
        }
        catch (Exception ex)
        {
            Trace($"UpdateCheck: 失敗 {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// チャネルに応じた「入手できる最新バージョン」。取得できなければ null。
    /// </summary>
    internal static Task<Version?> FetchLatestVersionAsync() => PackageContext.Channel switch
    {
        InstallChannel.Winget => FetchWingetLatestVersionAsync(),
        InstallChannel.Zip => FetchGitHubLatestVersionAsync(),
        _ => Task.FromResult<Version?>(null),
    };

    /// <summary>
    /// winget show --versions の出力から最新バージョンを取得する。
    /// 失敗時は null(winget 未インストール、ネットワーク不通、パース失敗など)。
    /// 出力のテキストパースは書式変更に弱いが、公式の代替 API(COM 相互運用)は
    /// 重すぎるため、失敗したら「確認できなかった」に倒れる前提で許容する。
    /// </summary>
    private static async Task<Version?> FetchWingetLatestVersionAsync()
    {
        var winget = FindWinget();
        if (winget is null) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = winget,
                Arguments = $"show {WingetId} --versions --disable-interactivity",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) return null;

            // "---" 区切り線より後の行からバージョンを抽出(降順表示なので先頭が最新)
            var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var separatorIndex = Array.FindIndex(lines, l => l.StartsWith("---"));
            if (separatorIndex < 0) return null;

            for (var i = separatorIndex + 1; i < lines.Length; i++)
            {
                if (Version.TryParse(lines[i], out var version)) return version;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// GitHub Releases の最新タグ(v1.0.5 形式)からバージョンを取得する。
    /// 失敗時は null(ネットワーク不通、レート制限、パース失敗など)。
    /// </summary>
    private static async Task<Version?> FetchGitHubLatestVersionAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            req.Headers.TryAddWithoutValidation("User-Agent", "Petapeta");    // GitHub API は User-Agent 必須
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var resp = await CheckHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag)) return null;

            var text = tag.GetString()?.TrimStart('v', 'V');
            return Version.TryParse(text, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? FindWinget()
    {
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(candidate)) return candidate;

        return Environment.GetEnvironmentVariable("PATH")
            ?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "winget.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// winget upgrade を切り離して起動する。呼び出し元はこの後アプリを終了すること。
    ///
    /// 実行中の exe を winget は置き換えられないため終了が先。XTimelineViewer は
    /// 固定 2 秒待ちだったが、終了(設定保存など)が 2 秒を超えると実行中の exe を
    /// 掴んだまま走って失敗するレースがある。Wait-Process で自プロセスの終了を
    /// 確実に待ってから実行する。コンソールはあえて表示し、進捗を見えるようにする。
    /// </summary>
    internal static void LaunchDetachedWingetUpgrade()
    {
        Trace("UpdateCheck: winget upgrade を起動してアプリを終了する");
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Wait-Process -Id "
                + Environment.ProcessId
                + $" -Timeout 60 -ErrorAction SilentlyContinue; winget upgrade --id {WingetId}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// 更新まわりの常時トレース。差し替えは UI の無い仕上げ役プロセスでも走るため、
    /// LogToFile 設定に関係なく Logs\update.log へ残す(肥大したら作り直す)。
    /// </summary>
    internal static void Trace(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsPath);
            var path = Path.Combine(AppPaths.LogsPath, "update.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
            {
                File.Delete(path);
            }
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // トレースの失敗は本処理に影響させない
        }
    }
}
