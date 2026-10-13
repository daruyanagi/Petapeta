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
    private static bool _registered;

    /// <summary>起動時に一度呼ぶ。登録できなくてもアプリの動作は止めない。</summary>
    public static void Initialize()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            LogFileService.Append($"{DateTime.Now:HH:mm:ss} [notification] 登録に失敗: {ex.GetType().Name} (0x{ex.HResult:X8})");
        }
    }

    /// <summary>設定でオンのときだけ通知を出す。失敗しても本処理に影響させない。</summary>
    public static void Show(string body)
    {
        if (!_registered || !SettingsService.ToastEnabled)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("Petapeta")
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
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
