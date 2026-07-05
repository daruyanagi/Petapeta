using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Petapeta.ViewModels;

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
}
