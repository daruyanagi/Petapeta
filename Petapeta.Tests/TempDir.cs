namespace Petapeta.Tests;

/// <summary>テストごとに使い捨てる一時ディレクトリ。</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "petapeta-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(string name)
    {
        var dir = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // 一時ファイルの削除失敗は無視(OS の掃除に任せる)
        }
    }
}
