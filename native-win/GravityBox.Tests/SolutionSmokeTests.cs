using GravityBox.Win32;
using Xunit;

namespace GravityBox.Tests;

public sealed class SolutionSmokeTests
{
    [Fact]
    public void SolutionSkeleton_CompilesAndMatchesBaselineVersion()
    {
        Assert.Equal("1.1.4", Win32BridgeInfo.BridgeVersion);
    }
}
