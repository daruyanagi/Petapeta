namespace Petapeta.Services;

/// <summary>
/// テスト用スタブ。本体の UpdateService は WinUI(App / R)に依存するため
/// リンクせず、リンク対象のサービス群が参照するメンバーだけを提供する。
/// </summary>
internal static class UpdateService
{
    internal const string LatestReleaseApi =
        "https://api.github.com/repos/daruyanagi/Petapeta/releases/latest";

    internal static readonly List<string> TraceLines = new();

    internal static void Trace(string message)
    {
        lock (TraceLines)
        {
            TraceLines.Add(message);
        }
    }
}
