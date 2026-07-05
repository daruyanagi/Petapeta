using System;
using System.IO;
using System.Text.Json;

namespace Petapeta.Services;

/// <summary>
/// アプリ設定。パッケージ有無に依存しないよう JSON ファイルに永続化する
/// (パッケージ環境では LocalState、アンパッケージドでは %LOCALAPPDATA%\Petapeta)。
/// </summary>
public static class SettingsService
{
    private sealed class Model
    {
        public bool ImageEnabled { get; set; } = true;
        public bool TextEnabled { get; set; } = true;
        public int RetentionDays { get; set; } = 7;
        public int MaxFiles { get; set; } = 100;
        public string Theme { get; set; } = "System";
        public bool StartMinimized { get; set; }
        public string Language { get; set; } = "";
        public bool SoundEnabled { get; set; } = true;
        public string SoundEvent { get; set; } = "Notification.Default";
    }

    private static readonly object Gate = new();
    private static readonly Model Values = Load();

    public static bool ImageEnabled
    {
        get => Values.ImageEnabled;
        set { Values.ImageEnabled = value; Save(); }
    }

    public static bool TextEnabled
    {
        get => Values.TextEnabled;
        set { Values.TextEnabled = value; Save(); }
    }

    public static int RetentionDays
    {
        get => Values.RetentionDays;
        set { Values.RetentionDays = value; Save(); }
    }

    public static int MaxFiles
    {
        get => Values.MaxFiles;
        set { Values.MaxFiles = value; Save(); }
    }

    /// <summary>"System" / "Light" / "Dark"</summary>
    public static string Theme
    {
        get => Values.Theme;
        set { Values.Theme = value; Save(); }
    }

    /// <summary>起動時にウィンドウを表示せずトレイ常駐で始めるか。</summary>
    public static bool StartMinimized
    {
        get => Values.StartMinimized;
        set { Values.StartMinimized = value; Save(); }
    }

    /// <summary>UI 言語の上書き。空文字はシステム設定に従う。BCP-47(例 "ja", "en-US")。</summary>
    public static string Language
    {
        get => Values.Language;
        set { Values.Language = value; Save(); }
    }

    /// <summary>ファイル形式を追加できたときにシステム音を鳴らすか。</summary>
    public static bool SoundEnabled
    {
        get => Values.SoundEnabled;
        set { Values.SoundEnabled = value; Save(); }
    }

    /// <summary>鳴らす通知音(ms-winsoundevent トークン)。</summary>
    public static string SoundEvent
    {
        get => Values.SoundEvent;
        set { Values.SoundEvent = value; Save(); }
    }

    private static Model Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                return JsonSerializer.Deserialize<Model>(File.ReadAllText(AppPaths.SettingsPath)) ?? new Model();
            }
        }
        catch
        {
            // 壊れた設定ファイルは既定値で作り直す
        }
        return new Model();
    }

    private static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                File.WriteAllText(
                    AppPaths.SettingsPath,
                    JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 保存失敗は致命的でないため無視(次回の変更で再試行)
            }
        }
    }
}
