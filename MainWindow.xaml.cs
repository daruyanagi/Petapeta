using System;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Petapeta.Views;

namespace Petapeta;

/// <summary>
/// The application window. Hosts the navigation shell and the tray icon.
/// Closing the window hides it to the tray; the app keeps monitoring
/// the clipboard until exit is chosen from the tray menu.
/// </summary>
public sealed partial class MainWindow : Window
{
    // トレイメニュー(SecondWindow モード)は別スレッドで動くため、
    // ウィンドウ操作は必ずこのキュー経由でメインスレッドへ渡す
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    private bool _exitRequested;
    private bool _initializingMonitorSwitch;

    public MainWindow()
    {
        InitializeComponent();

        _dispatcherQueue = DispatcherQueue;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // アンパッケージド実行では作業ディレクトリが不定のため絶対パスで指定する
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // 初期ウィンドウサイズ 800x600(DPI に合わせて物理ピクセルへ換算)
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(800 * scale), (int)(600 * scale)));

        // 「X」で閉じてもアプリは終了せずトレイに常駐する
        AppWindow.Closing += (_, e) =>
        {
            if (!_exitRequested)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };

        TrayIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        TrayIcon.ForceCreate();

        _initializingMonitorSwitch = true;
        MonitorSwitch.IsOn = App.Monitor.IsEnabled;
        _initializingMonitorSwitch = false;
        TrayMonitorItem.IsChecked = App.Monitor.IsEnabled;

        // 監視状態はどのUI(ツールバー/トレイ)から変えても
        // ここで一元的に両方のUIへ反映する
        App.Monitor.EnabledChanged += OnMonitorEnabledChanged;

        RootFrame.Navigate(typeof(LogPage));
    }

    private void OnMonitorEnabledChanged(bool enabled)
    {
        // ツールバー(メインスレッド)へ反映
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (MonitorSwitch.IsOn != enabled)
            {
                _initializingMonitorSwitch = true;
                MonitorSwitch.IsOn = enabled;
                _initializingMonitorSwitch = false;
            }
        });

        // トレイのチェックにも反映。SecondWindow モードでは項目が
        // 別スレッドに属することがあるため、項目自身のキューを優先する
        var trayQueue = TrayMonitorItem.DispatcherQueue ?? _dispatcherQueue;
        trayQueue.TryEnqueue(() =>
        {
            if (TrayMonitorItem.IsChecked != enabled)
            {
                TrayMonitorItem.IsChecked = enabled;
            }
        });
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var pageType = item.Tag switch
        {
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(LogPage),
        };

        if (RootFrame.CurrentSourcePageType != pageType)
        {
            RootFrame.Navigate(pageType);
        }
    }

    private void OnMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (_initializingMonitorSwitch)
        {
            return;
        }

        ApplyMonitoring(MonitorSwitch.IsOn);
    }

    // トレイメニューが開くたびに、現在の監視状態をチェック表示へ反映する
    // (常時同期の保険。スレッド跨ぎで失敗しても落とさない)
    private void OnTrayMenuOpening(object sender, object e)
    {
        try
        {
            TrayMonitorItem.IsChecked = App.Monitor.IsEnabled;
        }
        catch
        {
        }
    }

    private void OnTrayMonitorToggle(object sender, RoutedEventArgs e)
    {
        // ToggleMenuFlyoutItem はクリックで IsChecked が反転済み
        ApplyMonitoring(TrayMonitorItem.IsChecked);
    }

    /// <summary>監視状態を切り替える。反映は EnabledChanged を通じて各UIへ伝わる。</summary>
    private void ApplyMonitoring(bool enabled)
    {
        App.Monitor.IsEnabled = enabled;
        App.Monitor.Note(R.Get(enabled ? "LogMonitorResumed" : "LogMonitorPaused"));
    }

    private void ShowWindow()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show();
            Activate();
        });
    }

    private void OnTrayOpenClick(object sender, RoutedEventArgs e) => ShowWindow();

    private void OnTrayStagingClick(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start("explorer.exe", Services.AppPaths.EnsureStaging());
    }

    private void OnTrayExitClick(object sender, RoutedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _exitRequested = true;
            TrayIcon.Dispose();
            Application.Current.Exit();
        });
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
