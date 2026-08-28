using Petapeta.Services;
using Xunit;

namespace Petapeta.Tests;

public class FileNamingTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void FirstCall_UsesPlainName()
    {
        var path = FileNaming.CreateUniquePath(_dir.Path, "Clipboard 2026-08-25 123247", ".png");
        Assert.Equal("Clipboard 2026-08-25 123247.png", Path.GetFileName(path));
        Assert.True(File.Exists(path));   // CreateNew で即予約される
    }

    [Fact]
    public void Collision_AppendsSequenceNumbers()
    {
        var first = FileNaming.CreateUniquePath(_dir.Path, "base", ".png");
        var second = FileNaming.CreateUniquePath(_dir.Path, "base", ".png");
        var third = FileNaming.CreateUniquePath(_dir.Path, "base", ".png");

        Assert.Equal("base.png", Path.GetFileName(first));
        Assert.Equal("base (2).png", Path.GetFileName(second));
        Assert.Equal("base (3).png", Path.GetFileName(third));
    }

    [Fact]
    public void DifferentExtensions_DoNotCollide()
    {
        FileNaming.CreateUniquePath(_dir.Path, "base", ".png");
        var txt = FileNaming.CreateUniquePath(_dir.Path, "base", ".txt");
        Assert.Equal("base.txt", Path.GetFileName(txt));
    }
}
