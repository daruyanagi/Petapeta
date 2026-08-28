using Petapeta.Services;
using Xunit;

namespace Petapeta.Tests;

public class UpdateSwapTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string MakeApp(string name, string content)
    {
        var dir = _dir.Sub(name);
        File.WriteAllText(Path.Combine(dir, "Petapeta.exe"), content);
        File.WriteAllText(Path.Combine(dir, "Petapeta.dll"), content);
        return dir;
    }

    // ── FinishArgs ──────────────────────────────────────────────────────

    [Fact]
    public void FinishArgs_RoundTrip()
    {
        var args = UpdateSwap.BuildFinishArgs(@"C:\apps\Petapeta", 1234);
        var parsed = UpdateSwap.ParseFinishArgs(args);
        Assert.NotNull(parsed);
        Assert.Equal(@"C:\apps\Petapeta", parsed.Value.InstallDir);
        Assert.Equal(1234, parsed.Value.WaitForPid);
    }

    [Theory]
    [InlineData("--finish-update")]
    [InlineData("--finish-update", @"C:\apps")]
    [InlineData("--finish-update", @"C:\apps", "not-a-pid")]
    [InlineData("--finish-update", "", "12")]
    [InlineData("--startup")]
    public void ParseFinishArgs_RejectsMalformed(params string[] args) =>
        Assert.Null(UpdateSwap.ParseFinishArgs(args));

    [Fact]
    public void ParseFinishArgs_NormalStartup_IsNotMistaken()
    {
        Assert.Null(UpdateSwap.ParseFinishArgs(Array.Empty<string>()));
        Assert.Null(UpdateSwap.ParseFinishArgs(new[] { "--startup" }));
    }

    // ── Swap ────────────────────────────────────────────────────────────

    [Fact]
    public void Swap_Succeeds_AndKeepsBackup()
    {
        var install = MakeApp("install", "old");
        var staging = MakeApp("install.update", "new");

        var result = UpdateSwap.Swap(install, staging, attempts: 2, delayMs: 10);

        Assert.Equal(UpdateSwap.SwapResult.Succeeded, result);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Petapeta.exe")));
        // 旧版は .old に退避されている(掃除は次回起動時)
        Assert.Equal("old", File.ReadAllText(
            Path.Combine(ZipUpdater.BackupDirFor(install), "Petapeta.exe")));
    }

    [Fact]
    public void Swap_InvalidStaging_RollsBackToOldVersion()
    {
        var install = MakeApp("install", "old");
        var staging = _dir.Sub("install.update");
        File.WriteAllText(Path.Combine(staging, "Petapeta.exe"), "broken");   // dll 欠け

        var result = UpdateSwap.Swap(install, staging, attempts: 2, delayMs: 10);

        Assert.Equal(UpdateSwap.SwapResult.RolledBack, result);
        // 旧版がそのまま使える状態に戻っている
        Assert.True(ZipUpdater.LooksLikeApp(install));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "Petapeta.exe")));
    }

    [Fact]
    public void Swap_MissingInstallDir_DoesNotThrow()
    {
        var staging = MakeApp("install.update", "new");
        var result = UpdateSwap.Swap(Path.Combine(_dir.Path, "missing"), staging, attempts: 2, delayMs: 10);
        Assert.Equal(UpdateSwap.SwapResult.RolledBack, result);   // 何も動かしていない
    }

    // ── MoveWithRetry / CleanupBackup ───────────────────────────────────

    [Fact]
    public void MoveWithRetry_MissingSource_ThrowsImmediately() =>
        Assert.Throws<DirectoryNotFoundException>(() =>
            UpdateSwap.MoveWithRetry(Path.Combine(_dir.Path, "none"), Path.Combine(_dir.Path, "dest"),
                attempts: 2, delayMs: 10));

    [Fact]
    public void CleanupBackup_RemovesOldAndUpdateDirs()
    {
        var install = MakeApp("install", "cur");
        MakeApp("install.old", "old");
        MakeApp("install.update", "new");

        UpdateSwap.CleanupBackup(install);

        Assert.True(Directory.Exists(install));
        Assert.False(Directory.Exists(ZipUpdater.BackupDirFor(install)));
        Assert.False(Directory.Exists(ZipUpdater.StagingDirFor(install)));
    }

    [Fact]
    public async Task WaitForExit_AlreadyExited_ReturnsTrue()
    {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        await proc.WaitForExitAsync();

        Assert.True(await UpdateSwap.WaitForExitAsync(proc.Id, TimeSpan.FromSeconds(5)));
    }
}
