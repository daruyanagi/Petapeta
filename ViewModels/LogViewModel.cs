using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Petapeta.Services;

namespace Petapeta.ViewModels;

/// <summary>クリップボード処理のログを表示するだけの ViewModel。</summary>
public partial class LogViewModel : ObservableObject
{
    private const int MaxLogEntries = 200;

    private readonly ClipboardMonitorService _service = App.Monitor;
    private DispatcherQueue? _dispatcherQueue;

    public ObservableCollection<string> Logs { get; } = new();

    /// <summary>ページ表示後に一度だけ呼ぶ。UI スレッドの DispatcherQueue を受け取る。</summary>
    public void Start(DispatcherQueue dispatcherQueue)
    {
        if (_dispatcherQueue is not null)
        {
            return;
        }

        _dispatcherQueue = dispatcherQueue;

        // ウィンドウ表示前(自動起動時など)に溜まったログを反映してから購読する
        foreach (var line in _service.GetBacklog())
        {
            InsertLine(line);
        }
        _service.Log += line => _dispatcherQueue?.TryEnqueue(() => InsertLine(line));
    }

    private void InsertLine(string line)
    {
        Logs.Insert(0, line);
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        System.IO.Directory.CreateDirectory(AppPaths.LogsPath);
        System.Diagnostics.Process.Start("explorer.exe", AppPaths.LogsPath);
    }
}
