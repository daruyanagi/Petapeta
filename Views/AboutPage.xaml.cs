using Microsoft.UI.Xaml.Controls;

namespace Petapeta.Views;

/// <summary>バージョン情報と謝辞。</summary>
public sealed partial class AboutPage : Page
{
    public string VersionText { get; }

    public AboutPage()
    {
        // パッケージ有無に依存しないよう、アセンブリのバージョンを表示する
        var v = typeof(App).Assembly.GetName().Version;
        VersionText = R.F("AboutVersionFmt", v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}");
        InitializeComponent();
    }
}
