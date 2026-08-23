using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Petapeta.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Petapeta.Views;

/// <summary>
/// クリップボード処理のログ一覧。ログ購読を1本に保つため
/// ナビゲーションでインスタンスをキャッシュする。
/// </summary>
public sealed partial class LogPage : Page
{
    public LogViewModel ViewModel { get; } = new();

    public LogPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += (_, _) => ViewModel.Start(DispatcherQueue);
    }

    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CopyLines(LogList.SelectedItems.Cast<string>());
        args.Handled = true;
    }

    private void OnCopySelectedClick(object sender, RoutedEventArgs e) =>
        CopyLines(LogList.SelectedItems.Cast<string>());

    private void OnCopyAllClick(object sender, RoutedEventArgs e) =>
        CopyLines(ViewModel.Logs);

    private static void CopyLines(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
