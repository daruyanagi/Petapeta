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
    }
}
