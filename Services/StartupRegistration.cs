using System;
using Microsoft.Win32;

namespace Petapeta.Services;

/// <summary>
/// アンパッケージド実行時のサインイン自動開始。
/// HKCU\...\Run のエントリで管理する(パッケージ版は StartupTask を使う)。
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Petapeta";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("実行ファイルのパスを取得できません");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
