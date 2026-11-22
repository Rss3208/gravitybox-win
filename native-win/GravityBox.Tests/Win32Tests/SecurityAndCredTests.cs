using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using GravityBox.Win32;
using Xunit;

namespace GravityBox.Tests.Win32Tests;

public class SecurityAndCredTests
{
    [Fact]
    [Trait("Category", "Win32SecurityCred")]
    public void AuthenticodeVerifier_VerifiesSignedSystemBinary_And_RejectsUnsignedTempFile()
    {
        string systemExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var signedResult = AuthenticodeVerifier.Verify(systemExe);
        Assert.NotEqual("NotFound", signedResult.Status);
        Assert.False(string.IsNullOrWhiteSpace(signedResult.FileVersion));

        // 1. 真实未签名 PE 程序集（GravityBox.Win32.dll）应返回 NotSigned
        string unsignedPe = typeof(AuthenticodeVerifier).Assembly.Location;
        var unsignedPeResult = AuthenticodeVerifier.Verify(unsignedPe);
        Assert.False(unsignedPeResult.Valid);
        Assert.Equal("NotSigned", unsignedPeResult.Status);

        // 2. 损坏/非 PE 临时文件应返回 Valid == false
        string tempFile = Path.Combine(Path.GetTempPath(), $"gb-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tempFile, "MZ-not-a-valid-signed-binary");
        try
        {
            var corruptResult = AuthenticodeVerifier.Verify(tempFile);
            Assert.False(corruptResult.Valid);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    [Trait("Category", "Win32SecurityCred")]
    public void CredentialStore_ReadWriteDelete_WithTestPrefix_And_Enforces2560ByteLimit()
    {
        string testTarget = $"gravitybox-test:{Guid.NewGuid():N}";
        string sampleJson = "{\"token\":{\"access_token\":\"ya29.test\",\"refresh_token\":\"1//test-refresh\"},\"id_token_email\":\"demo@example.test\"}";

        try
        {
            Assert.Null(CredentialStore.ReadGeneric(testTarget));

            CredentialStore.WriteGeneric(testTarget, "antigravity", sampleJson);
            var readBack = CredentialStore.ReadGeneric(testTarget);
            Assert.NotNull(readBack);
            Assert.Equal(testTarget, readBack!.Target);
            Assert.Equal("antigravity", readBack.UserName);
            Assert.Equal(sampleJson, readBack.Secret);

            // 验证超过 2560 字节的凭据写入被安全拒绝
            string oversized = new('x', CredentialStore.MaxCredentialBlobBytes + 10);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CredentialStore.WriteGeneric(testTarget, "antigravity", oversized));

            bool deletedFirst = CredentialStore.DeleteGeneric(testTarget);
            Assert.True(deletedFirst);
            Assert.Null(CredentialStore.ReadGeneric(testTarget));

            // 幂等二次删除返回 false 不抛异常
            bool deletedSecond = CredentialStore.DeleteGeneric(testTarget);
            Assert.False(deletedSecond);
        }
        finally
        {
            try { CredentialStore.DeleteGeneric(testTarget); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Win32SecurityCred")]
    public void SecurityAclHelper_HardensDirectoryAndFileToCurrentUserAndSystemOnly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"gb-acl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempFile = Path.Combine(tempDir, "secret.token");
        File.WriteAllText(tempFile, "token-content", Encoding.UTF8);

        try
        {
            SecurityAclHelper.HardenPathAcl(tempDir, isDirectory: true);
            SecurityAclHelper.HardenPathAcl(tempFile, isDirectory: false);

            using var identity = WindowsIdentity.GetCurrent();
            var userSid = identity.User!;
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            var dirSec = new DirectoryInfo(tempDir).GetAccessControl();
            Assert.True(dirSec.AreAccessRulesProtected);
            var dirRules = dirSec.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();
            Assert.All(dirRules, rule =>
                Assert.True(rule.IdentityReference.Equals(userSid) || rule.IdentityReference.Equals(systemSid)));

            var verifyDir = SecurityAclHelper.VerifyOwnerAndReparsePoint(tempDir);
            Assert.True(verifyDir.Exists);
            Assert.False(verifyDir.IsReparsePoint);
            Assert.True(verifyDir.OwnedByCurrentUser);

            var verifyFile = SecurityAclHelper.VerifyOwnerAndReparsePoint(tempFile);
            Assert.True(verifyFile.Exists);
            Assert.False(verifyFile.IsReparsePoint);
            Assert.True(verifyFile.OwnedByCurrentUser);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
