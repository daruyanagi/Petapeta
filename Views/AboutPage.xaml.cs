using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace Petapeta.Views;

/// <summary>バージョン情報と謝辞。</summary>
public sealed partial class AboutPage : Page
{
    public string VersionText { get; }

    public AboutPage()
    {
        var v = Package.Current.Id.Version;
        VersionText = R.F("AboutVersionFmt", $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
        InitializeComponent();
    }
}
