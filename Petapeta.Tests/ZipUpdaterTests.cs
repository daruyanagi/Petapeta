using System.IO.Compression;
using System.Runtime.InteropServices;
using Petapeta.Services;
using Xunit;

namespace Petapeta.Tests;

public class ZipUpdaterTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private const string Sha256OfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    // ── ArchSuffix ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(Architecture.X64, "win-x64")]
    [InlineData(Architecture.Arm64, "win-arm64")]
    public void ArchSuffix_Supported(Architecture arch, string expected) =>
        Assert.Equal(expected, ZipUpdater.ArchSuffix(arch));

    [Fact]
    public void ArchSuffix_X86_Throws() =>
        Assert.Throws<PlatformNotSupportedException>(() => ZipUpdater.ArchSuffix(Architecture.X86));

    // ── SelectAsset ─────────────────────────────────────────────────────

    private const string FullReleaseJson = """
        {"assets":[
          {"name":"Petapeta-v1.0.6-win-x64.zip","browser_download_url":"https://example.com/x64.zip"},
          {"name":"Petapeta-v1.0.6-win-x64.zip.sha256","browser_download_url":"https://example.com/x64.sha256"},
          {"name":"Petapeta-v1.0.6-win-arm64.zip","browser_download_url":"https://example.com/a64.zip"},
          {"name":"Petapeta-v1.0.6-win-arm64.zip.sha256","browser_download_url":"https://example.com/a64.sha256"}
        ]}
        """;

    [Fact]
    public void SelectAsset_PicksMatchingArchitecture()
    {
        var asset = ZipUpdater.SelectAsset(FullReleaseJson, Architecture.X64);
        Assert.NotNull(asset);
        Assert.Equal("Petapeta-v1.0.6-win-x64.zip", asset.ZipName);
        Assert.Equal("https://example.com/x64.zip", asset.ZipUrl);
        Assert.Equal("https://example.com/x64.sha256", asset.ChecksumUrl);

        var arm = ZipUpdater.SelectAsset(FullReleaseJson, Architecture.Arm64);
        Assert.Equal("https://example.com/a64.zip", arm!.ZipUrl);
    }

    [Fact]
    public void SelectAsset_WithoutChecksum_IsNull()
    {
        // .sha256 の無いリリース(v1.0.4 以前)は検証できないため対象外
        const string json = """
            {"assets":[
              {"name":"Petapeta-v1.0.4-win-x64.zip","browser_download_url":"https://example.com/x64.zip"}
            ]}
            """;
        Assert.Null(ZipUpdater.SelectAsset(json, Architecture.X64));
    }

    [Fact]
    public void SelectAsset_EmptyAssets_IsNull() =>
        Assert.Null(ZipUpdater.SelectAsset("""{"assets":[]}""", Architecture.X64));

    [Fact]
    public void SelectAsset_NoAssetsProperty_IsNull() =>
        Assert.Null(ZipUpdater.SelectAsset("""{"tag_name":"v1.0.6"}""", Architecture.X64));

    // ── ParseChecksum ───────────────────────────────────────────────────

    [Fact]
    public void ParseChecksum_Sha256SumFormat() =>
        Assert.Equal(Sha256OfAbc, ZipUpdater.ParseChecksum($"{Sha256OfAbc}  Petapeta-v1.0.6-win-x64.zip"));

    [Fact]
    public void ParseChecksum_UppercaseIsNormalized() =>
        Assert.Equal(Sha256OfAbc, ZipUpdater.ParseChecksum(Sha256OfAbc.ToUpperInvariant()));

    [Fact]
    public void ParseChecksum_SkipsNonHashLines() =>
        Assert.Equal(Sha256OfAbc, ZipUpdater.ParseChecksum($"# comment\r\n{Sha256OfAbc}\tfile.zip\r\n"));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash file.zip")]
    [InlineData("abcdef")]
    public void ParseChecksum_Garbage_IsNull(string content) =>
        Assert.Null(ZipUpdater.ParseChecksum(content));

    // ── パス決定 ────────────────────────────────────────────────────────

    [Fact]
    public void StagingAndBackupDirs_AreSiblings()
    {
        Assert.Equal(@"C:\apps\Petapeta.update", ZipUpdater.StagingDirFor(@"C:\apps\Petapeta"));
        Assert.Equal(@"C:\apps\Petapeta.update", ZipUpdater.StagingDirFor(@"C:\apps\Petapeta\"));
        Assert.Equal(@"C:\apps\Petapeta.old", ZipUpdater.BackupDirFor(@"C:\apps\Petapeta"));
    }

    // ── LooksLikeApp / CanWriteTo ───────────────────────────────────────

    [Fact]
    public void LooksLikeApp_RequiresExeAndDll()
    {
        var dir = _dir.Sub("app");
        Assert.False(ZipUpdater.LooksLikeApp(dir));

        File.WriteAllText(Path.Combine(dir, "Petapeta.exe"), "x");
        Assert.False(ZipUpdater.LooksLikeApp(dir));

        File.WriteAllText(Path.Combine(dir, "Petapeta.dll"), "x");
        Assert.True(ZipUpdater.LooksLikeApp(dir));
    }

    [Fact]
    public void CanWriteTo_WritableDir_IsTrue() =>
        Assert.True(ZipUpdater.CanWriteTo(_dir.Path));

    [Fact]
    public void CanWriteTo_MissingDir_IsFalse() =>
        Assert.False(ZipUpdater.CanWriteTo(Path.Combine(_dir.Path, "no-such-dir")));

    // ── Extract / ComputeSha256 ─────────────────────────────────────────

    [Fact]
    public void Extract_RecreatesDirtyDestination()
    {
        var src = _dir.Sub("src");
        File.WriteAllText(Path.Combine(src, "Petapeta.exe"), "new");
        var zip = Path.Combine(_dir.Path, "app.zip");
        ZipFile.CreateFromDirectory(src, zip);

        var dest = _dir.Sub("dest");
        File.WriteAllText(Path.Combine(dest, "stale.txt"), "old");   // 前回の失敗の残骸

        ZipUpdater.Extract(zip, dest);

        Assert.True(File.Exists(Path.Combine(dest, "Petapeta.exe")));
        Assert.False(File.Exists(Path.Combine(dest, "stale.txt")));
    }

    [Fact]
    public async Task ComputeSha256_MatchesKnownVector()
    {
        var file = Path.Combine(_dir.Path, "abc.bin");
        await File.WriteAllTextAsync(file, "abc");
        Assert.Equal(Sha256OfAbc, await ZipUpdater.ComputeSha256Async(file));
    }
}
