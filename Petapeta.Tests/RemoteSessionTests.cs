using Petapeta.Services;
using Xunit;

namespace Petapeta.Tests;

public class RemoteSessionTests
{
    [Fact]
    public void InRemoteSession_RdpclipOwner_IsIgnored() =>
        Assert.True(RemoteSession.ShouldIgnore(isRemoteSession: true, ignoreEnabled: true, "rdpclip"));

    [Fact]
    public void OwnerComparison_IsCaseInsensitive() =>
        Assert.True(RemoteSession.ShouldIgnore(isRemoteSession: true, ignoreEnabled: true, "RdpClip"));

    [Fact]
    public void InRemoteSession_LocalApp_IsProcessed() =>
        Assert.False(RemoteSession.ShouldIgnore(isRemoteSession: true, ignoreEnabled: true, "msedge"));

    [Fact]
    public void InRemoteSession_UnknownOwner_IsProcessed() =>
        Assert.False(RemoteSession.ShouldIgnore(isRemoteSession: true, ignoreEnabled: true, null));

    [Fact]
    public void NotRemoteSession_RdpclipOwner_IsProcessed()
    {
        // ローカル(クライアント)側では所有者が mstsc/rdpclip でも処理する。
        // リモート側に Petapeta が無いときにローカルでファイル化する価値があるため
        Assert.False(RemoteSession.ShouldIgnore(isRemoteSession: false, ignoreEnabled: true, "rdpclip"));
        Assert.False(RemoteSession.ShouldIgnore(isRemoteSession: false, ignoreEnabled: true, "mstsc"));
    }

    [Fact]
    public void SettingOff_DisablesRule() =>
        Assert.False(RemoteSession.ShouldIgnore(isRemoteSession: true, ignoreEnabled: false, "rdpclip"));
}
