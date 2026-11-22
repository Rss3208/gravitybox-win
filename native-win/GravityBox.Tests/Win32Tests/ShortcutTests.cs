using System;
using System.IO;
using GravityBox.Win32;
using Xunit;

namespace GravityBox.Tests.Win32Tests;

public class ShortcutTests
{
    [Fact]
    [Trait("Category", "Win32Shortcut")]
    public void ShortcutManager_ParsesAndUpdatesCjkShortcutWithoutCorruptingIconOrWorkingDir()
    {
        string baseTemp = Path.Combine(Path.GetTempPath(), $"gb-lnk-测试 空格-{Guid.NewGuid():N}");
        string appDir = Path.Combine(baseTemp, "Programs", "Antigravity");
        string desktopDir = Path.Combine(baseTemp, "桌面 快捷方式");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(desktopDir);

        string officialExe = Path.Combine(appDir, "Antigravity.exe");
        File.WriteAllText(officialExe, "MZ-FAKE-EXE");
        string iconFile = Path.Combine(appDir, "antigravity.ico");
        File.WriteAllText(iconFile, "ICO");

        string lnkPath = Path.Combine(desktopDir, "Antigravity 正式版.lnk");
        string insidersLnkPath = Path.Combine(desktopDir, "Antigravity - Insiders.lnk");

        try
        {
            ShortcutManager.CreateShortcut(
                lnkPath: lnkPath,
                targetPath: officialExe,
                arguments: "--reuse-window",
                workingDirectory: appDir,
                description: "Antigravity Official",
                iconPath: iconFile,
                iconIndex: 2);

            ShortcutManager.CreateShortcut(
                lnkPath: insidersLnkPath,
                targetPath: officialExe,
                arguments: "",
                workingDirectory: appDir);

            // 1. 扫描快捷方式：应精准命中正式版，自动过滤 Insiders
            var scanned = ShortcutManager.ScanShortcuts(
                officialExePath: officialExe,
                customSearchRoots: new[] { (desktopDir, "desktop") });

            Assert.Single(scanned);
            Assert.Equal(lnkPath, scanned[0].Path);
            Assert.Equal("desktop", scanned[0].Location);
            Assert.Equal("--reuse-window", scanned[0].Arguments);
            Assert.Equal(appDir, scanned[0].WorkingDirectory);
            Assert.False(scanned[0].HasDebugPort);

            // 2. 更新 Arguments 追加 --remote-debugging-port=0，验证工作目录与图标位置零损坏
            ShortcutManager.SetShortcutArguments(lnkPath, "--reuse-window --remote-debugging-port=0");

            var updated = ShortcutManager.InspectShortcut(lnkPath, "desktop");
            Assert.NotNull(updated);
            Assert.Equal("--reuse-window --remote-debugging-port=0", updated!.Arguments);
            Assert.True(updated.HasDebugPort);
            Assert.Equal(appDir, updated.WorkingDirectory);
            Assert.Equal(officialExe, updated.TargetPath, ignoreCase: true);

            var (savedIconPath, savedIconIdx) = ShortcutManager.ReadShortcutIconLocation(lnkPath);
            Assert.Equal(iconFile, savedIconPath, ignoreCase: true);
            Assert.Equal(2, savedIconIdx);

            // 3. 还原原始 Arguments
            ShortcutManager.SetShortcutArguments(lnkPath, "--reuse-window");
            var restored = ShortcutManager.InspectShortcut(lnkPath, "desktop");
            Assert.NotNull(restored);
            Assert.Equal("--reuse-window", restored!.Arguments);
            Assert.False(restored.HasDebugPort);
        }
        finally
        {
            try { Directory.Delete(baseTemp, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Win32Shortcut")]
    public void SystemLocaleHelper_ReturnsNonEmptyBcp47Languages()
    {
        var locales = SystemLocaleHelper.GetPreferredUiLanguages();
        Assert.NotEmpty(locales);
        Assert.All(locales, loc => Assert.False(string.IsNullOrWhiteSpace(loc)));
    }
}
