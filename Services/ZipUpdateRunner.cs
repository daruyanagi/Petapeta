using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Petapeta.Services;

/// <summary>
/// ZIP 版の自前更新の段取り(#12)。XTimelineViewer の同名クラスの移植。
///
/// 「落として検証して展開する」のは <see cref="ZipUpdater"/>、
/// 「差し替える」のは <see cref="UpdateSwap"/>。ここはその二つを繋ぎ、
/// <b>仕上げ役として新しいバージョンを起動する</b>ところまでを受け持つ。
/// </summary>
internal static class ZipUpdateRunner
{
    /// <summary>自前更新ができるか。できない理由も返す。</summary>
    internal enum Eligibility
    {
        Ok,
        /// <summary>MSIX 版。Store / Windows Update に任せる。</summary>
        Packaged,
        /// <summary>winget 版。管理情報とズレるので winget に任せる。</summary>
        ManagedByWinget,
        /// <summary>インストール先(または隣にステージングを作る親)に書き込めない。</summary>
        NotWritable,
    }

    /// <summary>
    /// この環境で自前更新をしてよいか。
    /// 駄目なときはボタンを「リリースページを開く」のままにする。
    /// </summary>
    internal static Eligibility CheckEligibility(
        InstallChannel channel, bool isPackaged, string installDir)
    {
        if (isPackaged) return Eligibility.Packaged;
        if (channel == InstallChannel.Winget) return Eligibility.ManagedByWinget;
        if (!ZipUpdater.CanWriteTo(installDir)) return Eligibility.NotWritable;

        // ステージング(.update)とバックアップ(.old)はインストール先の
        // 「隣」= 親フォルダーに作る。親に書けなければ後で必ず失敗するので
        // ここで弾く(XTimelineViewer はインストール先しか見ていなかった)。
        var parent = Path.GetDirectoryName(installDir.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null || !ZipUpdater.CanWriteTo(parent)) return Eligibility.NotWritable;

        return Eligibility.Ok;
    }

    /// <summary>更新の実行結果。</summary>
    internal enum RunResult
    {
        /// <summary>展開まで済み、仕上げ役を起動した。呼び出し元はアプリを終了する。</summary>
        ReadyToRestart,
        /// <summary>このリリースは自前更新の対象外(.sha256 が無いなど)。</summary>
        NotSupported,
        /// <summary>途中で失敗した。何も置き換えていない。</summary>
        Failed,
        /// <summary>利用者が取り消した。</summary>
        Canceled,
    }

    /// <summary>
    /// 落として検証して展開し、仕上げ役を起動する。
    ///
    /// ここまでで<b>インストール先には一切触れていない</b>。
    /// 実際の差し替えは仕上げ役(新しいバージョン)が行う。
    /// </summary>
    internal static async Task<RunResult> RunAsync(
        HttpClient http,
        string installDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var updater = new ZipUpdater(http);

            var json = await updater.DownloadTextAsync(UpdateService.LatestReleaseApi, ct);
            var asset = ZipUpdater.SelectAsset(json, RuntimeInformation.ProcessArchitecture);
            if (asset is null)
            {
                // .sha256 の無いリリースは対象外。検証できないものは扱わない。
                UpdateService.Trace("ZipUpdateRunner: 検証できる資産が見つからない(対象外)");
                return RunResult.NotSupported;
            }

            var staging = await updater.StageAsync(asset, installDir, progress, ct);

            var newExe = Path.Combine(staging, "Petapeta.exe");
            var args = UpdateSwap.BuildFinishArgs(installDir, Environment.ProcessId);

            Process.Start(new ProcessStartInfo
            {
                FileName = newExe,
                WorkingDirectory = staging,
                UseShellExecute = false,
                ArgumentList = { args[0], args[1], args[2] },
            });

            UpdateService.Trace($"ZipUpdateRunner: 仕上げ役を起動した {newExe}");
            return RunResult.ReadyToRestart;
        }
        catch (OperationCanceledException)
        {
            UpdateService.Trace("ZipUpdateRunner: 取り消された");
            return RunResult.Canceled;
        }
        catch (Exception ex)
        {
            UpdateService.Trace($"ZipUpdateRunner: 失敗 {ex}");
            return RunResult.Failed;
        }
    }
}
