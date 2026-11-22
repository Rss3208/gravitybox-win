using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace GravityBox.Win32;

public sealed record ShortcutEntryInfo(
    string Path,
    string Location,
    string Name,
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    bool HasDebugPort
);

public static class ShortcutManager
{
    private const uint STGM_READ = 0x00000000;
    private const uint STGM_READWRITE = 0x00000002;
    private const uint SLGP_RAWPATH = 0x00000004;

    private static readonly Regex DebugPortRegex = new(
        @"(?:^|\s)--remote-debugging-port(?:=\d+|\s+\d+)(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public static IReadOnlyList<ShortcutEntryInfo> ScanShortcuts(
        string? officialExePath = null,
        IEnumerable<(string DirectoryPath, string LocationTag)>? customSearchRoots = null)
    {
        var results = new List<ShortcutEntryInfo>();
        string? normalizedOfficialExe = string.IsNullOrWhiteSpace(officialExePath)
            ? null
            : NormalizePath(officialExePath);

        var roots = customSearchRoots != null
            ? new List<(string DirectoryPath, string LocationTag)>(customSearchRoots)
            : GetDefaultSearchRoots();

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (dirPath, locationTag) in roots)
        {
            if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
            {
                continue;
            }

            IEnumerable<string> lnkFiles;
            try
            {
                lnkFiles = Directory.EnumerateFiles(dirPath, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (string lnkPath in lnkFiles)
            {
                string fullLnk = NormalizePath(lnkPath);
                if (!seenFiles.Add(fullLnk))
                {
                    continue;
                }

                var inspected = InspectShortcut(fullLnk, locationTag);
                if (inspected == null)
                {
                    continue;
                }

                // 排除 Insiders 快捷方式（INV-05）
                if (IsInsidersShortcut(inspected))
                {
                    continue;
                }

                if (normalizedOfficialExe != null)
                {
                    string targetNorm = NormalizePath(inspected.TargetPath);
                    if (!string.Equals(targetNorm, normalizedOfficialExe, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                else
                {
                    string targetFileName = Path.GetFileName(inspected.TargetPath ?? string.Empty);
                    if (!string.Equals(targetFileName, "Antigravity.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                results.Add(inspected);
            }
        }

        return results;
    }

    public static ShortcutEntryInfo? InspectShortcut(string lnkPath, string location = "custom")
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
        {
            return null;
        }

        object? comObj = null;
        try
        {
            comObj = new ShellLinkCoClass();
            var link = (IShellLinkW)comObj;
            var persist = (IPersistFile)comObj;
            persist.Load(Path.GetFullPath(lnkPath), (int)STGM_READ);

            var targetSb = new StringBuilder(1024);
            link.GetPath(targetSb, targetSb.Capacity, IntPtr.Zero, 0);
            string targetPath = targetSb.ToString().Trim();
            if (string.IsNullOrEmpty(targetPath))
            {
                link.GetPath(targetSb, targetSb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
                targetPath = Environment.ExpandEnvironmentVariables(targetSb.ToString().Trim());
            }

            var argsSb = new StringBuilder(2048);
            link.GetArguments(argsSb, argsSb.Capacity);
            string arguments = argsSb.ToString();

            var workDirSb = new StringBuilder(1024);
            link.GetWorkingDirectory(workDirSb, workDirSb.Capacity);
            string workingDir = workDirSb.ToString();

            string name = Path.GetFileNameWithoutExtension(lnkPath);
            bool hasDebugPort = DebugPortRegex.IsMatch(arguments ?? string.Empty);

            return new ShortcutEntryInfo(
                Path: Path.GetFullPath(lnkPath),
                Location: location,
                Name: name,
                TargetPath: targetPath,
                Arguments: arguments ?? string.Empty,
                WorkingDirectory: workingDir ?? string.Empty,
                HasDebugPort: hasDebugPort
            );
        }
        catch
        {
            return null;
        }
        finally
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                Marshal.FinalReleaseComObject(comObj);
            }
        }
    }

    public static void CreateShortcut(
        string lnkPath,
        string targetPath,
        string arguments = "",
        string workingDirectory = "",
        string description = "",
        string iconPath = "",
        int iconIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(lnkPath)) throw new ArgumentException("Shortcut path is required.", nameof(lnkPath));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path is required.", nameof(targetPath));

        string fullLnk = Path.GetFullPath(lnkPath);
        string? parent = Path.GetDirectoryName(fullLnk);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        object? comObj = null;
        try
        {
            comObj = new ShellLinkCoClass();
            var link = (IShellLinkW)comObj;
            link.SetPath(Path.GetFullPath(targetPath));
            link.SetArguments(arguments ?? string.Empty);
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                link.SetWorkingDirectory(workingDirectory);
            }
            if (!string.IsNullOrEmpty(description))
            {
                link.SetDescription(description);
            }
            if (!string.IsNullOrEmpty(iconPath))
            {
                link.SetIconLocation(iconPath, iconIndex);
            }

            var persist = (IPersistFile)comObj;
            persist.Save(fullLnk, true);
        }
        finally
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                Marshal.FinalReleaseComObject(comObj);
            }
        }
    }

    public static void SetShortcutArguments(string lnkPath, string newArguments)
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
        {
            throw new FileNotFoundException("Shortcut file not found.", lnkPath);
        }

        string fullLnk = Path.GetFullPath(lnkPath);
        object? comObj = null;
        try
        {
            comObj = new ShellLinkCoClass();
            var link = (IShellLinkW)comObj;
            var persist = (IPersistFile)comObj;
            persist.Load(fullLnk, (int)STGM_READWRITE);
            link.SetArguments(newArguments ?? string.Empty);
            persist.Save(fullLnk, true);
        }
        finally
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                Marshal.FinalReleaseComObject(comObj);
            }
        }
    }

    public static (string IconPath, int IconIndex) ReadShortcutIconLocation(string lnkPath)
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
        {
            return (string.Empty, 0);
        }

        object? comObj = null;
        try
        {
            comObj = new ShellLinkCoClass();
            var link = (IShellLinkW)comObj;
            var persist = (IPersistFile)comObj;
            persist.Load(Path.GetFullPath(lnkPath), (int)STGM_READ);
            var sb = new StringBuilder(1024);
            link.GetIconLocation(sb, sb.Capacity, out int iconIndex);
            return (sb.ToString(), iconIndex);
        }
        finally
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                Marshal.FinalReleaseComObject(comObj);
            }
        }
    }

    private static List<(string DirectoryPath, string LocationTag)> GetDefaultSearchRoots()
    {
        var roots = new List<(string, string)>();
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
        {
            roots.Add((desktop, "desktop"));
        }

        string startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrEmpty(startMenuPrograms))
        {
            roots.Add((startMenuPrograms, "start-menu"));
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            string taskbarPinned = Path.Combine(
                appData,
                "Microsoft",
                "Internet Explorer",
                "Quick Launch",
                "User Pinned",
                "TaskBar");
            roots.Add((taskbarPinned, "taskbar"));
        }

        return roots;
    }

    private static bool IsInsidersShortcut(ShortcutEntryInfo entry)
    {
        return (entry.Name?.Contains("Insiders", StringComparison.OrdinalIgnoreCase) ?? false)
            || (entry.TargetPath?.Contains("Insiders", StringComparison.OrdinalIgnoreCase) ?? false)
            || (entry.Path?.Contains("Insiders", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
        try
        {
            return Path.GetFullPath(rawPath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return rawPath.Trim();
        }
    }
}
