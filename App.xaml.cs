using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Petapeta.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Petapeta;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// クリップボード監視サービス。ウィンドウの表示状態に依存させないため
    /// App が所有し、起動直後から動かす。
    /// </summary>
    public static ClipboardMonitorService Monitor { get; } = new();

    /// <summary>最前面ウィンドウの監視(テキストのオンデマンドファイル化用)。</summary>
    public static ForegroundWatcher Foreground { get; } = new();

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        // リソース解決の前に UI 言語の上書きを反映する
        var language = Services.SettingsService.Language;
        if (!string.IsNullOrEmpty(language))
        {
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        }

        InitializeComponent();

        // クラッシュ原因の調査用に、未処理例外を Logs\crash-*.log へ記録する(#6)。
        // 0xc000027b(Stowed Exception)対策として3系統すべてを張る
        UnhandledException += (_, e) =>
            Services.LogFileService.WriteCrash("Xaml.Application.UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Services.LogFileService.WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.LogFileService.WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 更新の仕上げ役として起動されたときは、UI もインスタンス登録もせずに
        // 差し替えだけ行って終わる(#12)。多重起動防止より先に分岐しないと、
        // 終了中の旧インスタンスへリダイレクトして何もせず消えてしまう。
        if (UpdateSwap.ParseFinishArgs(Environment.GetCommandLineArgs()[1..]) is { } finish)
        {
            _ = FinishUpdateAsync(finish.InstallDir, finish.WaitForPid);
            return;
        }

        // 多重起動防止: 2つ目以降のインスタンスは既存インスタンスへ
        // アクティベーションを渡して即終了する
        var mainInstance = AppInstance.FindOrRegisterForKey("main");
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (!mainInstance.IsCurrent)
        {
            RedirectActivationTo(mainInstance, activationArgs);
            Environment.Exit(0);
            return;
        }

        AppInstance.GetCurrent().Activated += OnInstanceActivated;

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        ApplyTheme(Services.SettingsService.Theme);
        Monitor.Start();
        Foreground.ExplorerForegroundChanged += Monitor.OnExplorerForegroundChanged;
        Foreground.Start();

        if (!PackageContext.IsPackaged)
        {
            // 前回の更新で残った .old / .update を片付ける。消せなくても支障は無い
            UpdateSwap.CleanupBackup(AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }
        _ = UpdateService.RunBackgroundLoopAsync();

        // スタートアップ(自動起動)または「最小化で起動」設定のときは
        // ウィンドウを出さずトレイのみで開始する
        var launchedAtStartup = Environment.GetCommandLineArgs()
            .Contains(Services.StartupRegistration.StartupArgument, StringComparer.OrdinalIgnoreCase);
        var startHidden = launchedAtStartup || Services.SettingsService.StartMinimized;
        if (!startHidden)
        {
            Window.Activate();
        }
    }

    /// <summary>
    /// 旧プロセスの終了を待って差し替え、本来の場所から起動し直す(#12)。
    /// このプロセスは展開先(インストール先の隣)から動いており、
    /// 差し替え対象の外にいるので、実行中の exe / DLL を掴んでいない。
    ///
    /// 失敗しても旧版が起動できる状態に戻すのが最優先。
    /// 「更新できなかった」はやり直せるが、「更新に失敗して壊れた」は戻せない。
    /// </summary>
    private static async Task FinishUpdateAsync(string installDir, int waitForPid)
    {
        try
        {
            var staging = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            UpdateService.Trace($"FinishUpdate: staging={staging} install={installDir} pid={waitForPid}");

            // 待ちきれないまま差し替えると、掴まれたままのファイルを動かすことになる
            if (await UpdateSwap.WaitForExitAsync(waitForPid, TimeSpan.FromSeconds(30)))
            {
                var result = UpdateSwap.Swap(installDir, staging);
                UpdateService.Trace($"FinishUpdate: {result}");
            }
            else
            {
                UpdateService.Trace("FinishUpdate: 旧プロセスが終わらないので差し替えを中止する");
            }
        }
        catch (Exception ex)
        {
            UpdateService.Trace($"FinishUpdate: 失敗 {ex}");
        }
        finally
        {
            // Broken のときは installDir に本体が無く起動も失敗するが、
            // ここで黙って終わるよりログと手がかりが残る方がよい
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(installDir, "Petapeta.exe"),
                    WorkingDirectory = installDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                UpdateService.Trace($"FinishUpdate(launch): 失敗 {ex}");
            }
            Environment.Exit(0);
        }
    }

    /// <summary>テーマ設定("System"/"Light"/"Dark")をウィンドウ全体に適用する。</summary>
    public static void ApplyTheme(string theme)
    {
        if (Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    /// <summary>2つ目のインスタンスが起動されたとき、既存ウィンドウを前面に出す。</summary>
    private void OnInstanceActivated(object? sender, AppActivationArguments e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Window.AppWindow.Show();
            Window.Activate();
        });
    }

    private static void RedirectActivationTo(AppInstance target, AppActivationArguments args)
    {
        // OnLaunched(UI スレッド)上で同期待ちするとデッドロックするため
        // 別スレッドでリダイレクトを実行して完了を待つ
        using var done = new SemaphoreSlim(0);
        _ = Task.Run(async () =>
        {
            try
            {
                await target.RedirectActivationToAsync(args);
            }
            finally
            {
                done.Release();
            }
        });
        done.Wait(TimeSpan.FromSeconds(5));
    }
}
