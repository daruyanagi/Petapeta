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

        AppWindow.SetIcon("Assets/AppIcon.ico");

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

        // 監視状態はどのUI(ツールバー/トレイ)から変えても
        // ここで一元的にツールバーへ反映する
        App.Monitor.EnabledChanged += OnMonitorEnabledChanged;

        RootFrame.Navigate(typeof(LogPage));
    }

    private void OnMonitorEnabledChanged(bool enabled)
    {
        // トレイ(別スレッド)からの変更もあるため、ツールバーのある
        // メインスレッドのキューへ確実に渡す
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (MonitorSwitch.IsOn != enabled)
            {
                _initializingMonitorSwitch = true;
                MonitorSwitch.IsOn = enabled;
                _initializingMonitorSwitch = false;
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
    private void OnTrayMenuOpening(object sender, object e)
    {
        TrayMonitorItem.IsChecked = App.Monitor.IsEnabled;
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
        _ = OpenStagingFolderAsync();
    }

    private static async System.Threading.Tasks.Task OpenStagingFolderAsync()
    {
        var folder = await Windows.Storage.ApplicationData.Current.LocalFolder
            .CreateFolderAsync("Staging", Windows.Storage.CreationCollisionOption.OpenIfExists);
        await Windows.System.Launcher.LaunchFolderAsync(folder);
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
