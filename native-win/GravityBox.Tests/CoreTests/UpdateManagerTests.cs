using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GravityBox.App.Core;
using Xunit;

namespace GravityBox.Tests.CoreTests;

[Trait("Category", "UpdateManager")]
public sealed class UpdateManagerTests : IDisposable
{
    private readonly string _tempRoot;

    public UpdateManagerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"gb-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }

    [Fact]
    public void VersionCompare_WorksCorrectly()
    {
        Assert.True(AppUpdateManager.CompareVersions("1.2.0", "1.1.3") > 0);
        Assert.Equal(0, AppUpdateManager.CompareVersions("v1.1.3", "1.1.3"));
        Assert.True(AppUpdateManager.CompareVersions("1.1.2", "1.1.3") < 0);
    }

    [Fact]
    public void DefaultReleasesEndpoint_PointsToRss3208GravityBoxWin()
    {
        using var mgr = new AppUpdateManager("1.1.3");
        Assert.Equal("https://api.github.com/repos/Rss3208/gravitybox-win/releases/latest", mgr.ReleasesApiUrl);
        Assert.Equal("https://github.com/Rss3208/gravitybox-win/releases", AppUpdateManager.DefaultReleasesPageUrl);
    }

    [Fact]
    public async Task CheckForUpdates_FallsBackToPortableZipWhenSetupAbsent()
    {
        string zipName = "GravityBox-1.2.0-win-x64-portable.zip";
        using var http = new HttpClient(new MockHttpHandler((req, _) =>
        {
            string json = $$"""
            {
              "tag_name": "v1.2.0",
              "html_url": "https://github.com/Rss3208/gravitybox-win/releases/tag/v1.2.0",
              "assets": [
                { "name": "{{zipName}}", "browser_download_url": "https://example.invalid/{{zipName}}" },
                { "name": "{{zipName}}.sha256", "browser_download_url": "https://example.invalid/{{zipName}}.sha256" }
              ]
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));

        using var mgr = new AppUpdateManager("1.1.3", "https://example.invalid/releases/latest", http);
        var rel = await mgr.CheckForUpdatesAsync();
        Assert.NotNull(rel);
        Assert.Equal(zipName, rel!.AssetFileName);
    }

    [Fact]
    public async Task DownloadAndVerify_AcceptsValidSha256_AndRejectsTamperedPayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("MZ-VALID-INSTALLER-BYTES");
        string validHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string fileName = "GravityBox-1.2.0-win-x64-setup.exe";

        string sidecarContent = $"{validHash}  {fileName}\n";
        using var httpOk = new HttpClient(new MockHttpHandler((req, _) =>
        {
            if (req.RequestUri!.AbsoluteUri.EndsWith("/latest"))
            {
                string json = $$"""
                {
                  "tag_name": "v1.2.0",
                  "html_url": "https://github.com/Rss3208/gravitybox-win/releases/tag/v1.2.0",
                  "assets": [
                    { "name": "{{fileName}}", "browser_download_url": "https://example.invalid/{{fileName}}" },
                    { "name": "{{fileName}}.sha256", "browser_download_url": "https://example.invalid/{{fileName}}.sha256" }
                  ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (req.RequestUri.AbsoluteUri.EndsWith(".sha256"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sidecarContent) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }));

        using var mgr = new AppUpdateManager("1.1.3", "https://example.invalid/releases/latest", httpOk);
        var rel = await mgr.CheckForUpdatesAsync();
        Assert.NotNull(rel);
        Assert.Equal("1.2.0", rel!.Version);

        string downloaded = await mgr.DownloadAndVerifyAsync(rel, _tempRoot);
        Assert.True(File.Exists(downloaded));
        Assert.Equal(UpdateStatusKind.ReadyToInstall, mgr.Status);

        // Tampered hash must be rejected
        string badSidecar = $"{new string('0', 64)}  {fileName}\n";
        using var httpBad = new HttpClient(new MockHttpHandler((req, _) =>
        {
            if (req.RequestUri!.AbsoluteUri.EndsWith(".sha256"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(badSidecar) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }));

        using var badMgr = new AppUpdateManager("1.1.3", "https://example.invalid/releases/latest", httpBad);
        await Assert.ThrowsAsync<InvalidDataException>(() => badMgr.DownloadAndVerifyAsync(rel, Path.Combine(_tempRoot, "bad")));
        Assert.Equal(UpdateStatusKind.Error, badMgr.Status);
    }
}
