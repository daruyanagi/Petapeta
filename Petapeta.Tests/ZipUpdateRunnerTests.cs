using Petapeta.Services;
using Xunit;

namespace Petapeta.Tests;

public class ZipUpdateRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Packaged_DelegatesToStore() =>
        Assert.Equal(ZipUpdateRunner.Eligibility.Packaged,
            ZipUpdateRunner.CheckEligibility(InstallChannel.Packaged, isPackaged: true, _dir.Path));

    [Fact]
    public void PackagedFlag_WinsOverChannel() =>
        Assert.Equal(ZipUpdateRunner.Eligibility.Packaged,
            ZipUpdateRunner.CheckEligibility(InstallChannel.Zip, isPackaged: true, _dir.Path));

    [Fact]
    public void Winget_DelegatesToWinget() =>
        Assert.Equal(ZipUpdateRunner.Eligibility.ManagedByWinget,
            ZipUpdateRunner.CheckEligibility(InstallChannel.Winget, isPackaged: false, _dir.Path));

    [Fact]
    public void Zip_WritableInstallAndParent_IsOk() =>
        Assert.Equal(ZipUpdateRunner.Eligibility.Ok,
            ZipUpdateRunner.CheckEligibility(InstallChannel.Zip, isPackaged: false, _dir.Sub("app")));

    [Fact]
    public void Zip_MissingInstallDir_IsNotWritable() =>
        Assert.Equal(ZipUpdateRunner.Eligibility.NotWritable,
            ZipUpdateRunner.CheckEligibility(
                InstallChannel.Zip, isPackaged: false, Path.Combine(_dir.Path, "no-such-dir")));
}
