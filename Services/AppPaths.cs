using System;
using System.IO;

namespace Petapeta.Services;

/// <summary>
/// アプリデータの置き場所。MSIX パッケージ環境では LocalState、
/// アンパッケージド(ZIP/winget 配布)では %LOCALAPPDATA%\Petapeta を使う。
/// </summary>
public static class AppPaths
{
    public static string DataRoot { get; } = PackageContext.IsPackaged
        ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Petapeta");

    public static string StagingPath { get; } = Path.Combine(DataRoot, "Staging");

    public static string SettingsPath { get; } = Path.Combine(DataRoot, "settings.json");

    public static string LogsPath { get; } = Path.Combine(DataRoot, "Logs");

    /// <summary>ステージングフォルダーを(なければ作って)返す。</summary>
    public static string EnsureStaging()
    {
        Directory.CreateDirectory(StagingPath);
        return StagingPath;
    }
}
