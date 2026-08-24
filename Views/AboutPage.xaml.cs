using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Petapeta.Services;

namespace Petapeta.Views;

/// <summary>バージョン情報と謝辞、更新の確認(#12)。</summary>
public sealed partial class AboutPage : Page
{
    public string VersionText { get; }

    private readonly bool _useWinget;
    private readonly bool _canSelfUpdate;
    private readonly string _installDir;

    private readonly Button _updateButton = new();
    private readonly Button _cancelButton = new();
    private readonly HyperlinkButton _releaseLink = new();
    private CancellationTokenSource? _cts;

    public AboutPage()
    {
        // パッケージ有無に依存しないよう、アセンブリのバージョンを表示する。
        // 配布経路(ZIP / winget / パッケージ)もここでラベル表示する(#12)
        var v = UpdateService.CurrentVersion;
        var channel = R.Get(PackageContext.Channel switch
        {
            InstallChannel.Winget => "AboutChannelWinget",
            InstallChannel.Packaged => "AboutChannelPackaged",
            _ => "AboutChannelZip",
        });
        VersionText = R.F("AboutVersionChannelFmt", $"{v.Major}.{v.Minor}.{v.Build}", channel);

        InitializeComponent();

        if (PackageContext.IsPackaged)
        {
            // MSIX 版の更新は Store / Windows Update に任せる
            UpdateSection.Visibility = Visibility.Collapsed;
            _installDir = string.Empty;
            return;
        }

        _installDir = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        _useWinget = PackageContext.Channel == InstallChannel.Winget && UpdateService.FindWinget() is not null;
        var eligibility = ZipUpdateRunner.CheckEligibility(
            PackageContext.Channel, PackageContext.IsPackaged, _installDir);
        _canSelfUpdate = !_useWinget && eligibility == ZipUpdateRunner.Eligibility.Ok;

        _updateButton.Content = _useWinget ? R.Get("UpdateViaWinget")
                              : _canSelfUpdate ? R.Get("UpdateRestartAndUpdate")
                                               : R.Get("UpdateOpenReleasePage");
        _updateButton.Click += OnUpdateActionClick;
        _cancelButton.Content = R.Get("UpdateCancelButton");
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _releaseLink.Content = R.Get("UpdateOpenReleasePage");
        _releaseLink.Click += (_, _) => OpenReleasePage();

        ShowInitialState();
        RefreshLastChecked();

        // ページ表示中にバックグラウンドの確認が走ったら反映する。
        // ページはナビゲーションのたびに作り直されるため、購読は必ず解除する
        Loaded += (_, _) => UpdateService.AvailabilityChanged += OnAvailabilityChanged;
        Unloaded += (_, _) => UpdateService.AvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_cts is not null)
        {
            return;   // ダウンロード中の表示をバックグラウンド確認で上書きしない
        }
        ShowInitialState();
        RefreshLastChecked();
    });

    private void ShowInitialState()
    {
        if (UpdateService.AvailableTag is { } tag)
        {
            ShowAvailable(tag);
        }
        else if (SettingsService.LastUpdateCheck is not null)
        {
            SetState(InfoBarSeverity.Success, R.Get("UpdateLatest"), _releaseLink);
        }
        else
        {
            // 一度も確認できていないうちは「最新」と断定しない
            SetState(InfoBarSeverity.Informational, R.Get("UpdateNotChecked"), _releaseLink);
        }
    }

    private void RefreshLastChecked() =>
        UpdateCard.Description = (SettingsService.LastUpdateCheck is { } checkedAt
            ? R.F("UpdateLastCheckedFmt", checkedAt.LocalDateTime.ToString("g"))
            : null)!;    // null は「説明行なし」の正規の指定(SettingsCard が行ごと隠す)

    /// <summary>表示の切り替えはここ 1 か所。進捗を渡さなければ進捗バーは隠れる。</summary>
    private void SetState(InfoBarSeverity severity, string message, ButtonBase? action, double? progress = null)
    {
        UpdateBar.Severity = severity;
        UpdateBar.Message = message;
        UpdateBar.ActionButton = action;
        DownloadProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is { } value) DownloadProgress.Value = value;
    }

    private void ShowAvailable(string tag) =>
        SetState(InfoBarSeverity.Warning, R.F("UpdateAvailableFmt", tag), _updateButton);

    private void OpenReleasePage() =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(UpdateService.ReleasesPageUrl));

    private async void OnCheckClick(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        SetState(InfoBarSeverity.Informational, R.Get("UpdateChecking"), null);
        try
        {
            var latest = await UpdateService.CheckOnceAsync();
            if (latest is null)
            {
                // 取得できなかったのに「最新です」と出すと、更新を見落とす
                SetState(InfoBarSeverity.Error, R.Get("UpdateErrorText"), _releaseLink);
                return;
            }
            ShowInitialState();
        }
        finally
        {
            RefreshLastChecked();
            CheckButton.IsEnabled = true;
        }
    }

    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_useWinget)
            {
                await RunWingetUpdateAsync();
            }
            else if (_canSelfUpdate)
            {
                await RunSelfUpdateAsync();
            }
            else
            {
                OpenReleasePage();
            }
        }
        catch (Exception ex)
        {
            UpdateService.Trace($"AboutPage: 更新操作に失敗 {ex}");
            SetState(InfoBarSeverity.Error, R.Get("UpdateErrorText"), _releaseLink);
        }
    }

    /// <summary>winget 版: 確認のうえアプリを終了し、winget upgrade に委譲する。</summary>
    private async Task RunWingetUpdateAsync()
    {
        if (!await ConfirmAsync(R.Get("UpdateWingetConfirmTitle"), R.Get("UpdateWingetConfirmBody"))) return;

        UpdateService.LaunchDetachedWingetUpgrade();
        ((MainWindow)App.Window).ExitForUpdate();
    }

    /// <summary>ZIP 版: ダウンロード → 検証 → 展開し、仕上げ役を起動して終了する。</summary>
    private async Task RunSelfUpdateAsync()
    {
        if (!await ConfirmAsync(R.Get("UpdateRestartConfirmTitle"), R.Get("UpdateRestartConfirmBody"))) return;

        CheckButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        SetState(InfoBarSeverity.Informational, R.Get("UpdateDownloading"), _cancelButton, 0);

        try
        {
            var result = await ZipUpdateRunner.RunAsync(
                UpdateService.DownloadHttp,
                _installDir,
                new Progress<double>(v => SetState(
                    InfoBarSeverity.Informational, R.Get("UpdateDownloading"), _cancelButton, v)),
                _cts.Token);

            switch (result)
            {
                case ZipUpdateRunner.RunResult.ReadyToRestart:
                    // ここから先は仕上げ役(新しいバージョン)の仕事。
                    // こちらが終わらないと、差し替えるファイルを掴んだままになる
                    ((MainWindow)App.Window).ExitForUpdate();
                    break;
                case ZipUpdateRunner.RunResult.NotSupported:
                    SetState(InfoBarSeverity.Warning,
                        R.F("UpdateSelfUpdateUnavailableFmt", UpdateService.AvailableTag ?? "?"), _releaseLink);
                    break;
                case ZipUpdateRunner.RunResult.Canceled:
                    ShowInitialState();
                    break;
                default:
                    SetState(InfoBarSeverity.Error, R.Get("UpdateErrorText"), _releaseLink);
                    break;
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            CheckButton.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string body)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = R.Get("UpdateConfirmButton"),
            CloseButtonText = R.Get("UpdateCancelButton"),
            XamlRoot = XamlRoot,
            RequestedTheme = ((FrameworkElement)App.Window.Content).ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
