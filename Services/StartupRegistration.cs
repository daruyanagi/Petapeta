using System;
using System.IO;
using Microsoft.Win32;

namespace Petapeta.Services;

/// <summary>
/// サインイン時の自動開始。スタートアップフォルダー(shell:startup)への
/// ショートカットで管理する。パッケージ版の StartupTask は開発モード登録だと
/// サインイン時に起動されないため使わない(#1)。
/// </summary>
public static class StartupRegistration
{
    /// <summary>アンパッケージド時にサイレント起動を伝えるコマンドライン引数。</summary>
    public const string StartupArgument = "--startup";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "Petapeta";

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Petapeta.lnk");

    public static bool IsEnabled()
    {
        if (File.Exists(ShortcutPath))
        {
            return true;
        }

        // v1.0.0(Run キー方式)からの移行判定
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(LegacyRunValueName) is not null;
    }

    public static void SetEnabled(bool enable)
    {
        RemoveLegacyRunValue();

        if (enable)
        {
            CreateShortcut();
        }
        else if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell を利用できません");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);

        if (PackageContext.IsPackaged)
        {
            // パッケージ版の exe は直接起動できないため、AUMID 経由で起動する
            var aumid = $"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!App";
            link.TargetPath = Path.Combine(
                Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "explorer.exe");
            link.Arguments = $"shell:AppsFolder\\{aumid}";
        }
        else
        {
            link.TargetPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("実行ファイルのパスを取得できません");
            link.Arguments = StartupArgument;
            link.WorkingDirectory = AppContext.BaseDirectory;
        }

        link.IconLocation = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico") + ",0";
        link.Description = "Petapeta";
        link.Save();
    }

    private static void RemoveLegacyRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 掃除に失敗しても本処理は続行する
        }
    }
}
