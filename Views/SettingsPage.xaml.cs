using Microsoft.UI.Xaml.Controls;
using Petapeta.ViewModels;

namespace Petapeta.Views;

/// <summary>設定ページ。ナビゲーションのたびに生成され、現在値を読み直す。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();

        // ページはナビゲーションごとに作り直されるため、破棄時に
        // サービス側イベントの購読を解除する(#19)
        Unloaded += (_, _) => ViewModel.Detach();
    }
}
