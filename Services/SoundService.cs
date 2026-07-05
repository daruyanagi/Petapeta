using System;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Petapeta.Services;

/// <summary>
/// Windows 標準の通知音を鳴らす。MediaPlayer は GC されると再生が途切れるため
/// 単一インスタンスを保持して使い回す。
/// </summary>
public static class SoundService
{
    /// <summary>選択肢に出す Windows 標準の通知音(ms-winsoundevent トークン)。</summary>
    public static readonly string[] SoundTokens =
    {
        "Notification.Default",
        "Notification.IM",
        "Notification.Mail",
        "Notification.Reminder",
        "Notification.SMS",
    };

    private static readonly MediaPlayer Player = new() { AudioCategory = MediaPlayerAudioCategory.Alerts };

    /// <summary>設定が有効なら、選択中の通知音を鳴らす。</summary>
    public static void PlayFeedback()
    {
        if (!SettingsService.SoundEnabled)
        {
            return;
        }

        Play(SettingsService.SoundEvent);
    }

    /// <summary>指定した通知音を鳴らす(テスト再生にも使う)。</summary>
    public static void Play(string token)
    {
        try
        {
            Player.Source = MediaSource.CreateFromUri(new Uri($"ms-winsoundevent:{token}"));
            Player.Play();
        }
        catch
        {
            // 音声デバイスがない等でも本処理は継続する
        }
    }
}
