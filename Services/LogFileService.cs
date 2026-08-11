using System;
using System.IO;

namespace Petapeta.Services;

/// <summary>
/// ログのファイル保存(#2)。UI を開かなくても後から調査できるよう、
/// 日付別ファイルに追記する。保持日数を過ぎた古いログは自動削除。
/// 書き込み失敗は本処理に影響させない。
/// </summary>
public static class LogFileService
{
    private static readonly object Gate = new();
    private static bool _cleaned;

    public static void Append(string line)
    {
        if (!SettingsService.LogToFile)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsPath);
                if (!_cleaned)
                {
                    Cleanup();
                    _cleaned = true;
                }
                var file = Path.Combine(AppPaths.LogsPath, $"petapeta-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // ログ保存の失敗は無視(次の書き込みで再試行)
        }
    }

    /// <summary>
    /// クラッシュ情報を専用ファイルに書き出す(#6)。診断目的のため
    /// LogToFile 設定に関係なく常に保存する。
    /// </summary>
    public static void WriteCrash(string origin, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsPath);
            var path = Path.Combine(AppPaths.LogsPath, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {origin}\r\n" +
                $"HResult: 0x{ex?.HResult:X8}\r\n" +
                $"{ex}\r\n");
        }
        catch
        {
            // クラッシュログの保存失敗は何もできないため無視
        }
    }

    /// <summary>保持日数(ステージングと同じ設定値)を過ぎたログを削除する。</summary>
    private static void Cleanup()
    {
        var cutoff = DateTime.Now.AddDays(-SettingsService.RetentionDays);
        foreach (var file in Directory.GetFiles(AppPaths.LogsPath, "petapeta-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 消せないファイルは次回に持ち越し
            }
        }
    }
}
