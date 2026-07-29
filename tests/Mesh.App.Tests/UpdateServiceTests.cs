using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Mesh.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests
{
    [TestClass]
    public sealed class UpdateServiceTests
    {
        private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [TestMethod]
        public void ProgressPercentDoesNotOverflow()
        {
            Assert.AreEqual(100, new UpdateProgress(UpdatePhase.Downloading, long.MaxValue, long.MaxValue, null).Percent);
            Assert.AreEqual(50, new UpdateProgress(UpdatePhase.Downloading, long.MaxValue / 2 + 1, long.MaxValue, null).Percent);
            Assert.AreEqual(0, new UpdateProgress(UpdatePhase.Downloading, -1, long.MaxValue, null).Percent);
            Assert.AreEqual(-1, new UpdateProgress(UpdatePhase.Downloading, 1, 0, null).Percent);
        }

        [TestMethod]
        [DataRow("v0.0.0", true)]
        [DataRow("v1.2.3", true)]
        [DataRow("1.2.3", false)]
        [DataRow("V1.2.3", false)]
        [DataRow("v01.2.3", false)]
        [DataRow("v1.2", false)]
        [DataRow("v1.2.3-beta", false)]
        [DataRow("v1.2.3+build", false)]
        [DataRow(" v1.2.3", false)]
        [DataRow("v1.2.3 ", false)]
        [DataRow("v1.x.3", false)]
        public void VersionParsingIsStrict(string value, bool expected)
        {
            Assert.AreEqual(expected, UpdateService.TryParseVersion(value, out _));
        }

        [TestMethod]
        public void StableReleaseRequiresExactAssetAndDigest()
        {
            var result = Parse(ReleaseJson());

            Assert.IsTrue(result.Available);
            Assert.IsNotNull(result.Info);
            Assert.AreEqual("Mesh-Setup-v999.2.3.zip", result.Info.AssetName);
            Assert.AreEqual(Digest, result.Info.Sha256);
        }

        [TestMethod]
        public void ReleaseRejectsMalformedAndPrereleaseMetadata()
        {
            Assert.IsFalse(Parse("{}").Available);
            Assert.IsFalse(Parse(ReleaseJson(tag: "v999.2")).Available);
            Assert.IsFalse(Parse(ReleaseJson(prerelease: true)).Available);
            Assert.IsFalse(Parse(ReleaseJson(draft: true)).Available);
            Assert.IsFalse(Parse(ReleaseJson(includeDigest: false)).Available);
            Assert.IsFalse(Parse(ReleaseJson(digest: "sha256:not-a-digest")).Available);
            Assert.IsFalse(Parse(ReleaseJson(digest: $"sha512:{Digest}")).Available);
        }

        [TestMethod]
        public void ReleaseRejectsWrongAssetHostAndRawExecutable()
        {
            Assert.IsFalse(Parse(ReleaseJson(assetName: "Mesh-Setup-v999.2.3.exe")).Available);
            Assert.IsFalse(Parse(ReleaseJson(assetName: "mesh-setup-v999.2.3.zip")).Available);
            Assert.IsFalse(Parse(ReleaseJson(host: "objects.githubusercontent.com")).Available);
            Assert.IsFalse(Parse(ReleaseJson(path: "/MeshRelayAI/Mesh/releases/download/v999.2.3/other.zip")).Available);
        }

        [TestMethod]
        public async Task ExtractionAllowsOnlyExpectedTopLevelInstaller()
        {
            var directory = NewTestDirectory();
            try
            {
                var archive = Path.Combine(directory, "valid.zip");
                CreateZip(archive, ("Mesh-Setup-v999.2.3.exe", "installer"));
                var info = Info(size: new FileInfo(archive).Length);

                var path = await UpdateService.ExtractInstallerAsync(
                    archive, directory, info, new ProgressSink(), CancellationToken.None);

                Assert.AreEqual("Mesh-Setup-v999.2.3.exe", Path.GetFileName(path));
                Assert.AreEqual("installer", await File.ReadAllTextAsync(path));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public async Task ExtractionRejectsTraversalNestedAndExtraEntries()
        {
            foreach (var entries in new[]
            {
                new[] { ("../Mesh-Setup-v999.2.3.exe", "bad") },
                new[] { ("nested/Mesh-Setup-v999.2.3.exe", "bad") },
                new[] { ("Mesh-Setup-v999.2.3.exe", "ok"), ("extra.txt", "bad") }
            })
            {
                var directory = NewTestDirectory();
                try
                {
                    var archive = Path.Combine(directory, "invalid.zip");
                    CreateZip(archive, entries);
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                        UpdateService.ExtractInstallerAsync(
                            archive, directory, Info(new FileInfo(archive).Length),
                            new ProgressSink(), CancellationToken.None));
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [TestMethod]
        public async Task ChecksAreSerialized()
        {
            handlerCalls = 0;
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new DelegateHandler(async (_, ct) =>
            {
                var requestNumber = Interlocked.Increment(ref handlerCalls);
                if (requestNumber == 1)
                {
                    firstEntered.SetResult();
                    await releaseFirst.Task.WaitAsync(ct);
                }
                return JsonResponse(ReleaseJson());
            });
            using var service = new UpdateService(
                new TestHttpClientFactory(handler), new TestAppControl(),
                NullLogger<UpdateService>.Instance, () => true);

            var first = service.CheckAsync();
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.CheckAsync();
            await Task.Delay(100);
            Assert.AreEqual(1, Volatile.Read(ref handlerCalls));

            releaseFirst.SetResult();
            await Task.WhenAll(first, second);
            Assert.AreEqual(2, Volatile.Read(ref handlerCalls));
        }

        [TestMethod]
        public void PublisherPredicateRequiresExactMeshPublisher()
        {
            Assert.IsTrue(UpdateService.IsMeshPublisher("CN=Quonkel, O=Quonkel"));
            Assert.IsFalse(UpdateService.IsMeshPublisher("CN=Quonkel Malware, O=Quonkel"));
            Assert.IsFalse(UpdateService.IsMeshPublisher("CN=Quonkel, O=Different Publisher"));
            Assert.IsFalse(UpdateService.IsMeshPublisher("CN=Quonkel"));
            Assert.IsFalse(UpdateService.IsMeshPublisher("CN=MeshRelayAI, O=MeshRelayAI"));
            Assert.IsFalse(UpdateService.IsMeshPublisher(null));
        }

        private static int handlerCalls;

        private static UpdateCheckResult Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return UpdateService.ParseLatestRelease(document.RootElement, new Version(1, 0, 0));
        }

        private static string ReleaseJson(
            string tag = "v999.2.3",
            string? assetName = null,
            string host = "github.com",
            string? path = null,
            bool prerelease = false,
            bool draft = false,
            bool includeDigest = true,
            string? digest = null)
        {
            assetName ??= $"Mesh-Setup-{tag}.zip";
            path ??= $"/MeshRelayAI/Mesh/releases/download/{tag}/{assetName}";
            digest ??= $"sha256:{Digest}";
            var digestProperty = includeDigest ? $@",""digest"":""{digest}""" : "";
            return $$"""
                {
                  "tag_name": "{{tag}}",
                  "draft": {{draft.ToString().ToLowerInvariant()}},
                  "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
                  "body": "notes",
                  "html_url": "https://github.com/MeshRelayAI/Mesh/releases/tag/{{tag}}",
                  "assets": [{
                    "name": "{{assetName}}",
                    "browser_download_url": "https://{{host}}{{path}}",
                    "size": 100{{digestProperty}}
                  }]
                }
                """;
        }

        private static UpdateInfo Info(long size) => new(
            new Version(999, 2, 3),
            "v999.2.3",
            "Mesh-Setup-v999.2.3.zip",
            "https://github.com/MeshRelayAI/Mesh/releases/download/v999.2.3/Mesh-Setup-v999.2.3.zip",
            size,
            Digest,
            null,
            null);

        private static string NewTestDirectory()
        {
            var directory = Path.Combine(
                AppContext.BaseDirectory, "UpdateServiceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void CreateZip(string path, params (string Name, string Content)[] entries)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(item.Content);
            }
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        private sealed class ProgressSink : IProgress<UpdateProgress>
        {
            public void Report(UpdateProgress value)
            {
            }
        }

        private sealed class DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                callback(request, cancellationToken);
        }

        private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }

        private sealed class TestAppControl : IAppControl
        {
            public void ShowMainWindow() { }
            public void Quit() { }
            public bool IsLaunchAtStartupEnabled() => false;
            public void SetLaunchAtStartup(bool enabled) { }
        }
    }
}

namespace Mesh.App.Services
{
    public interface IAppControl
    {
        void ShowMainWindow();
        void Quit();
        bool IsLaunchAtStartupEnabled();
        void SetLaunchAtStartup(bool enabled);
    }
}

namespace Microsoft.Maui.ApplicationModel
{
    public static class MainThread
    {
        public static void BeginInvokeOnMainThread(Action action) => action();
    }
}
