using System;
using System.Threading.Tasks;
using GravityBox.App.Core;
using Microsoft.Win32;
using Xunit;

namespace GravityBox.Tests.CoreTests;

[Trait("Category", "LifecycleSync")]
public sealed class LifecycleSyncTests
{
    private sealed class FakeLifecycleHost : ILifecycleHostAdapter
    {
        public bool Running { get; set; }
        public bool DebugPortReady { get; set; }
        public bool CloseCausesExit { get; set; } = true;
        public int CloseCalls { get; private set; }
        public int LaunchCalls { get; private set; }

        public bool IsAntigravityRunning(string officialExePath) => Running;
        public bool HasActiveDebugPort(string profileDir) => Running && DebugPortReady;

        public int CloseAntigravityGracefully(string officialExePath)
        {
            CloseCalls++;
            if (CloseCausesExit)
            {
                Running = false;
                DebugPortReady = false;
            }
            return 1;
        }

        public bool LaunchAntigravityWithDebugPort(string officialExePath)
        {
            LaunchCalls++;
            Running = true;
            DebugPortReady = true;
            return true;
        }
    }

    [Fact]
    public async Task SafeRelaunch_TimesOutGracefully_WithoutForceKill()
    {
        var host = new FakeLifecycleHost { Running = true, DebugPortReady = false, CloseCausesExit = false };
        using var svc = new LifecycleSyncService(host)
        {
            GraceWaitTimeout = TimeSpan.FromMilliseconds(120),
            PollInterval = TimeSpan.FromMilliseconds(20),
            PostExitBuffer = TimeSpan.FromMilliseconds(10),
        };

        var result = await svc.SafeRelaunchAsync(@"C:\Users\demo\AppData\Local\Programs\Antigravity\Antigravity.exe", @"C:\Users\demo\AppData\Roaming\Antigravity");

        Assert.False(result.Ok);
        Assert.True(result.TimedOut);
        Assert.Equal(1, host.CloseCalls);
        Assert.Equal(0, host.LaunchCalls);
        Assert.True(host.Running);
        Assert.True(svc.IsAutoQuitSuppressed);
    }

    [Fact]
    public async Task SafeRelaunch_ClosesAndRelaunchesWithDebugPort()
    {
        var host = new FakeLifecycleHost { Running = true, DebugPortReady = false, CloseCausesExit = true };
        using var svc = new LifecycleSyncService(host)
        {
            GraceWaitTimeout = TimeSpan.FromMilliseconds(200),
            PollInterval = TimeSpan.FromMilliseconds(20),
            PostExitBuffer = TimeSpan.FromMilliseconds(10),
        };

        var result = await svc.OpenAntigravityAsync(@"C:\Users\demo\AppData\Local\Programs\Antigravity\Antigravity.exe", @"C:\Users\demo\AppData\Roaming\Antigravity");

        Assert.True(result.Ok);
        Assert.False(result.TimedOut);
        Assert.Equal(1, host.CloseCalls);
        Assert.Equal(1, host.LaunchCalls);
        Assert.True(host.DebugPortReady);
    }

    [Fact]
    public async Task LifecycleSync_SuppressesAutoExitDuringRelaunch_AndFiresWhenExitedNormally()
    {
        var host = new FakeLifecycleHost { Running = true, DebugPortReady = true };
        using var svc = new LifecycleSyncService(host);
        int exitFired = 0;
        svc.AutoExitRequested += () => exitFired++;

        await svc.CheckLifecycleSyncTickAsync("Antigravity.exe", syncEnabled: true);
        svc.SuppressLifecycleAutoQuit(TimeSpan.FromSeconds(5));
        host.Running = false;
        await svc.CheckLifecycleSyncTickAsync("Antigravity.exe", syncEnabled: true);
        Assert.Equal(0, exitFired);

        // Clear suppression by creating a fresh service without suppression
        using var svc2 = new LifecycleSyncService(host);
        int exitFired2 = 0;
        svc2.AutoExitRequested += () => exitFired2++;
        host.Running = true;
        await svc2.CheckLifecycleSyncTickAsync("Antigravity.exe", syncEnabled: true);
        host.Running = false;
        await svc2.CheckLifecycleSyncTickAsync("Antigravity.exe", syncEnabled: true);
        Assert.Equal(1, exitFired2);
    }

    [Fact]
    public void LaunchAtLogin_WritesAndDeletesHkcuRunWithoutUac()
    {
        string testSubKey = $@"Software\GravityBoxTest\Run-{Guid.NewGuid():N}";
        try
        {
            using var svc = new LifecycleSyncService(new FakeLifecycleHost(), testSubKey);
            Assert.False(svc.IsLaunchAtLoginEnabled());

            svc.SetLaunchAtLogin(true, @"C:\Users\demo\AppData\Local\Programs\GravityBox\GravityBox.exe");
            Assert.True(svc.IsLaunchAtLoginEnabled());

            using (var key = Registry.CurrentUser.OpenSubKey(testSubKey, writable: false))
            {
                string? val = key?.GetValue(LifecycleSyncService.RunValueName) as string;
                Assert.Contains("--minimized", val);
            }

            svc.SetLaunchAtLogin(false);
            Assert.False(svc.IsLaunchAtLoginEnabled());
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\GravityBoxTest", throwOnMissingSubKey: false); } catch { }
        }
    }
}
