using System;
using System.Runtime.InteropServices;

namespace Petapeta.Services;

/// <summary>
/// リモートデスクトップ(RDP)セッション関連の判定(#53)。
/// テストからソースリンクで検証するため、設定や WinUI に依存させない。
/// </summary>
internal static class RemoteSession
{
    private const int SM_REMOTESESSION = 0x1000;

    /// <summary>RDP クライアントからクリップボードを中継するプロセス名。</summary>
    private const string RdpClipboardProcess = "rdpclip";

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>このプロセスが RDP セッション内(接続先側)で動いているか。</summary>
    internal static bool IsRemoteSession
    {
        get
        {
            try
            {
                return GetSystemMetrics(SM_REMOTESESSION) != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// このコピーを処理せず見送るべきか。
    ///
    /// RDP セッション内では、クライアント側で行われたコピーが rdpclip 経由で
    /// 届く。その貼り付け先はたいていクライアント側であり、こちらで付けた
    /// ファイル形式はリモートのパスを指すため、クライアント側の Petapeta と
    /// 取り合いになるか「項目が見つかりません」になる。クライアント側で
    /// 前面判定が働いていないのにこちらだけ反応する誤判定もここで止まる。
    /// </summary>
    internal static bool ShouldIgnore(bool isRemoteSession, bool ignoreEnabled, string? clipboardOwner) =>
        isRemoteSession
        && ignoreEnabled
        && string.Equals(clipboardOwner, RdpClipboardProcess, StringComparison.OrdinalIgnoreCase);
}
