using System;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Petapeta.Services;

/// <summary>
/// トースト通知(#56)。ログにしか残らなかった「スキップした」判断を、
/// その場でユーザーに知らせる。パッケージ版・アンパッケージド版の両方で
/// 動く Windows App SDK の AppNotification を使う。
/// </summary>
internal static class NotificationService
{
    /// <summary>通知のボタンが押されたときの引数「action」の値: 見送った画像をリサイズしてコピー(#59)。</summary>
    public const string ResizeAction = "resize";

    private const string ActionKey = "action";

    private static bool _registered;

    /// <summary>通知のボタンが押された(引数「action」の値)。UI スレッドとは限らない。</summary>
    public static event Action<string>? ActionInvoked;

    /// <summary>起動時に一度呼ぶ。登録できなくてもアプリの動作は止めない。</summary>
    public static void Initialize()
    {
        try
        {
            // ハンドラーの購読は Register より前に行う必要がある
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            LogFileService.Append($"{DateTime.Now:HH:mm:ss} [notification] 登録に失敗: {ex.GetType().Name} (0x{ex.HResult:X8})");
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue(ActionKey, out var action) && !string.IsNullOrEmpty(action))
        {
            ActionInvoked?.Invoke(action);
        }
    }

    /// <summary>設定でオンのときだけ通知を出す。失敗しても本処理に影響させない。</summary>
    public static void Show(string body) => Present(new AppNotificationBuilder().AddText(body));

    /// <summary>ボタン付きの通知。押されると <see cref="ActionInvoked"/> に action が届く。</summary>
    public static void ShowWithAction(string body, string buttonText, string action) =>
        Present(new AppNotificationBuilder()
            .AddText(body)
            .AddButton(new AppNotificationButton(buttonText).AddArgument(ActionKey, action)));

    private static void Present(AppNotificationBuilder builder)
    {
        if (!_registered || !SettingsService.ToastEnabled)
        {
            return;
        }

        try
        {
            // タイトルはシステムがアプリ名を出すので本文だけにする
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            LogFileService.Append($"{DateTime.Now:HH:mm:ss} [notification] 表示に失敗: {ex.GetType().Name} (0x{ex.HResult:X8})");
        }
    }

    /// <summary>終了時に呼ぶ。アンパッケージド版は登録を残すと次回起動時に不整合になり得る。</summary>
    public static void Shutdown()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch
        {
            // 終了時の後始末失敗は無視
        }
        _registered = false;
    }
}
