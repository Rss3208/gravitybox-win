using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GravityBox.Win32;
using Xunit;

namespace GravityBox.Tests.Win32Tests;

public class ProcessAndNetworkTests
{
    [Fact]
    [Trait("Category", "Win32ProcessNetwork")]
    public void ProcessInspector_ListsCurrentProcess_And_ChecksLivenessWithPidReuseGuard()
    {
        int currentPid = Environment.ProcessId;
        var list = ProcessInspector.ListProcesses();
        var self = list.FirstOrDefault(p => p.Pid == currentPid);

        Assert.NotNull(self);
        Assert.False(string.IsNullOrWhiteSpace(self!.ExecutablePath));
        Assert.False(string.IsNullOrWhiteSpace(self.CommandLine));
        Assert.True(self.CreationTimeMs > 0);

        var aliveExact = ProcessInspector.CheckProcessAlive(currentPid, self.CreationTimeMs);
        Assert.True(aliveExact.Alive);

        // 模拟 PID 复用：传入偏差 60 秒的期望启动时间，必须返回 Alive == false
        var aliveMismatched = ProcessInspector.CheckProcessAlive(currentPid, self.CreationTimeMs - 60_000);
        Assert.False(aliveMismatched.Alive);
    }

    [Fact]
    [Trait("Category", "Win32ProcessNetwork")]
    public void ProcessInspector_ReadsChildCommandLine_And_TargetEnvVarFromPeb()
    {
        string expectedToken = "gb_test_csrf_" + Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 15 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["ANTIGRAVITY_SERVER_CSRF_TOKEN"] = expectedToken;

        using var child = Process.Start(psi);
        Assert.NotNull(child);
        try
        {
            Thread.Sleep(150);
            string? readToken = ProcessInspector.ReadProcessEnvVar(child!.Id, "ANTIGRAVITY_SERVER_CSRF_TOKEN");
            Assert.Equal(expectedToken, readToken);

            var procs = ProcessInspector.ListProcesses("cmd");
            var matched = procs.FirstOrDefault(p => p.Pid == child.Id);
            Assert.NotNull(matched);
            Assert.Contains("127.0.0.1", matched!.CommandLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (!child!.HasExited) child.Kill(true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Win32ProcessNetwork")]
    public void NetworkInspector_FindsLoopbackListenerByPortAndPid()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            int currentPid = Environment.ProcessId;

            var byPort = NetworkInspector.FindTcpListeners(filterPort: port);
            Assert.Single(byPort);
            Assert.Equal(currentPid, byPort[0].Pid);
            Assert.Equal("127.0.0.1", byPort[0].LocalAddress);
            Assert.Equal(port, byPort[0].LocalPort);
            Assert.False(string.IsNullOrWhiteSpace(byPort[0].ExecutablePath));

            var byPid = NetworkInspector.FindTcpListeners(filterPid: currentPid);
            Assert.Contains(byPid, entry => entry.LocalPort == port && entry.LocalAddress == "127.0.0.1");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    [Trait("Category", "Win32ProcessNetwork")]
    public void JobObjectGuard_TerminatesAssignedChildWhenDisposed()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 30 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var child = Process.Start(psi);
        Assert.NotNull(child);
        int childPid = child!.Id;

        using (var job = new JobObjectGuard())
        {
            bool assigned = job.AssignProcess(child);
            Assert.True(assigned);
            Assert.False(child.HasExited);
        }

        // 释放 JobObject 后，被绑定的子进程应由 Windows 内核自动终止
        bool exited = child.WaitForExit(3000);
        Assert.True(exited);
        Assert.False(ProcessInspector.CheckProcessAlive(childPid).Alive);
    }
}
