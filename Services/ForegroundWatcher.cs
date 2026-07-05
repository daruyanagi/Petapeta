using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Petapeta.Services;

/// <summary>
/// 最前面ウィンドウの変化を監視し、それがエクスプローラー(デスクトップ含む)か
/// どうかを通知する。コールバックはフックを張ったスレッドのメッセージループで
/// 届くため、UI スレッドから Start すること。
/// </summary>
public sealed class ForegroundWatcher
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>最前面が変わったときに発火。true = エクスプローラー/デスクトップ。</summary>
    public event Action<bool>? ExplorerForegroundChanged;

    // ネイティブに渡すデリゲートは GC に回収されないようフィールドで保持する
    private WinEventDelegate? _callback;
    private nint _hook;

    public void Start()
    {
        if (_hook != 0)
        {
            return;
        }

        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>現在の最前面がエクスプローラー/デスクトップか。</summary>
    public static bool IsExplorerForeground() => IsExplorerWindow(GetForegroundWindow());

    private static bool IsExplorerWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var name = new StringBuilder(64);
        if (GetClassName(hwnd, name, name.Capacity) == 0)
        {
            return false;
        }

        // CabinetWClass = エクスプローラー、Progman / WorkerW = デスクトップ
        return name.ToString() is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";
    }

    private void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        ExplorerForegroundChanged?.Invoke(IsExplorerWindow(hwnd));
    }

    private delegate void WinEventDelegate(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);
}
