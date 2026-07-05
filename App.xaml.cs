using System;
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
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
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

        // スタートアップ(自動起動)または「最小化で起動」設定のときは
        // ウィンドウを出さずトレイのみで開始する
        var startHidden = activationArgs.Kind == ExtendedActivationKind.StartupTask
            || Services.SettingsService.StartMinimized;
        if (!startHidden)
        {
            Window.Activate();
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
