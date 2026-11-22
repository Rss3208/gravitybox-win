using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GravityBox.Win32;

public sealed record PathOwnershipVerificationInfo(
    bool Exists,
    bool IsReparsePoint,
    bool OwnedByCurrentUser
);

public static class SecurityAclHelper
{
    /// <summary>
    /// 禁用继承并仅授予当前登录用户 SID 与 SYSTEM 完全控制权限（ADR-008 / NFR-004）。
    /// </summary>
    public static void HardenPathAcl(string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? userSid = identity.User;
        if (userSid == null)
        {
            throw new InvalidOperationException("Unable to determine current Windows user SID.");
        }

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        if (isDirectory)
        {
            var dirInfo = new DirectoryInfo(fullPath);
            if (!dirInfo.Exists)
            {
                Directory.CreateDirectory(fullPath);
                dirInfo.Refresh();
            }

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                userSid,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        else
        {
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("Target file for ACL hardening does not exist.", fullPath);
            }

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                userSid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
        }
    }

    /// <summary>
    /// 校验目标路径是否存在、是否为符号链接/Junction 重解析点、以及所有者是否为当前用户或本地管理员组（02-design.md §4.2.2）。
    /// </summary>
    public static PathOwnershipVerificationInfo VerifyOwnerAndReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PathOwnershipVerificationInfo(false, false, false);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return new PathOwnershipVerificationInfo(false, false, false);
        }

        bool isDir = Directory.Exists(fullPath);
        bool isFile = !isDir && File.Exists(fullPath);
        if (!isDir && !isFile)
        {
            return new PathOwnershipVerificationInfo(false, false, false);
        }

        try
        {
            FileAttributes attrs = File.GetAttributes(fullPath);
            bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0;

            IdentityReference? ownerRef = isDir
                ? new DirectoryInfo(fullPath).GetAccessControl().GetOwner(typeof(SecurityIdentifier))
                : new FileInfo(fullPath).GetAccessControl().GetOwner(typeof(SecurityIdentifier));

            if (ownerRef is not SecurityIdentifier ownerSid)
            {
                return new PathOwnershipVerificationInfo(true, isReparse, false);
            }

            using var currentIdentity = WindowsIdentity.GetCurrent();
            SecurityIdentifier? currentUserSid = currentIdentity.User;
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            bool owned = (currentUserSid != null && ownerSid == currentUserSid)
                || ownerSid == adminsSid
                || ownerSid == systemSid;

            return new PathOwnershipVerificationInfo(true, isReparse, owned);
        }
        catch
        {
            return new PathOwnershipVerificationInfo(true, false, false);
        }
    }
}
