using Windows.Storage;

namespace Petapeta.Services;

/// <summary>アプリ設定。ApplicationData.LocalSettings に永続化する。</summary>
public static class SettingsService
{
    private static Windows.Foundation.Collections.IPropertySet Values =>
        ApplicationData.Current.LocalSettings.Values;

    public static bool ImageEnabled
    {
        get => Get(nameof(ImageEnabled), true);
        set => Values[nameof(ImageEnabled)] = value;
    }

    public static bool TextEnabled
    {
        get => Get(nameof(TextEnabled), true);
        set => Values[nameof(TextEnabled)] = value;
    }

    public static int RetentionDays
    {
        get => Get(nameof(RetentionDays), 7);
        set => Values[nameof(RetentionDays)] = value;
    }

    public static int MaxFiles
    {
        get => Get(nameof(MaxFiles), 100);
        set => Values[nameof(MaxFiles)] = value;
    }

    /// <summary>"System" / "Light" / "Dark"</summary>
    public static string Theme
    {
        get => Get(nameof(Theme), "System");
        set => Values[nameof(Theme)] = value;
    }

    /// <summary>起動時にウィンドウを表示せずトレイ常駐で始めるか。</summary>
    public static bool StartMinimized
    {
        get => Get(nameof(StartMinimized), false);
        set => Values[nameof(StartMinimized)] = value;
    }

    /// <summary>UI 言語の上書き。空文字はシステム設定に従う。BCP-47(例 "ja", "en-US")。</summary>
    public static string Language
    {
        get => Get(nameof(Language), "");
        set => Values[nameof(Language)] = value;
    }

    /// <summary>ファイル形式を追加できたときにシステム音を鳴らすか。</summary>
    public static bool SoundEnabled
    {
        get => Get(nameof(SoundEnabled), true);
        set => Values[nameof(SoundEnabled)] = value;
    }

    /// <summary>鳴らす通知音(ms-winsoundevent トークン)。</summary>
    public static string SoundEvent
    {
        get => Get(nameof(SoundEvent), "Notification.Default");
        set => Values[nameof(SoundEvent)] = value;
    }

    private static T Get<T>(string key, T defaultValue) =>
        Values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
}
