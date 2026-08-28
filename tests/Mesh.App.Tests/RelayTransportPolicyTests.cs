using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Xml.Linq;

namespace Mesh.App.Tests;

[TestClass]
public sealed class RelayTransportPolicyTests
{
    [DataTestMethod]
    [DataRow("https://meshrelay.net", false, true)]
    [DataRow("http://127.0.0.1:5080", true, true)]
    [DataRow("http://localhost:5080", true, true)]
    [DataRow("http://10.0.2.2:5080", true, true)]
    [DataRow("http://192.168.1.10:5080", true, false)]
    [DataRow("http://127.0.0.1:5080", false, false)]
    [DataRow("http://10.0.2.2:5080", false, false)]
    public void Transport_AllowsOnlyHttpsOrDebugLocalHttp(
        string value,
        bool allowLocalHttp,
        bool expected)
        => Assert.AreEqual(
            expected,
            RelayTransportPolicy.IsTransportAllowed(new Uri(value), allowLocalHttp));

    [TestMethod]
    public void ProductionManifest_DoesNotWidenCleartextTransport()
    {
        var root = FindRepositoryRoot();
        var production = XDocument.Load(Path.Combine(
            root, "src", "Mesh.App", "Platforms", "Android", "AndroidManifest.xml"));
        XNamespace android = "http://schemas.android.com/apk/res/android";
        var application = production.Root!.Element("application")!;

        Assert.IsNull(application.Attribute(android + "usesCleartextTraffic"));
        Assert.AreEqual(
            "@xml/mesh_network_security",
            application.Attribute(android + "networkSecurityConfig")?.Value);
        var releasePolicy = XDocument.Load(Path.Combine(
            root, "src", "Mesh.App", "Platforms", "Android", "mesh_network_security.release.xml"));
        Assert.AreEqual(
            "false",
            releasePolicy.Root!.Element("base-config")!
                .Attribute("cleartextTrafficPermitted")?.Value);
        Assert.IsFalse(releasePolicy.Root.Elements("domain-config").Any());
    }

    [TestMethod]
    public void DebugAndroidPolicy_IsHostScopedAndDefaultsToDeny()
    {
        var root = FindRepositoryRoot();
        var policy = XDocument.Load(Path.Combine(
            root, "src", "Mesh.App", "Platforms", "Android", "mesh_network_security.debug.xml"));
        var baseConfig = policy.Root!.Element("base-config")!;
        var domainConfig = policy.Root.Element("domain-config")!;
        var domains = domainConfig.Elements("domain").Select(element => element.Value).ToArray();

        Assert.AreEqual("false", baseConfig.Attribute("cleartextTrafficPermitted")?.Value);
        Assert.AreEqual("true", domainConfig.Attribute("cleartextTrafficPermitted")?.Value);
        CollectionAssert.AreEquivalent(
            new[] { "localhost", "127.0.0.1", "10.0.2.2" },
            domains);
        Assert.IsTrue(domainConfig.Elements("domain").All(
            element => element.Attribute("includeSubdomains")?.Value == "false"));
    }

    [TestMethod]
    public async Task StartupConnectionFailure_IsRecoverableAndUserActionable()
    {
        var diagnostics = new List<string>();
        var message = await StartupConnectionRecovery.TryConnectAsync(
            () => Task.FromException(new OnlineReplicationError(
                "Could not read Protocol 9 capabilities from the relay.")),
            diagnostics.Add);

        StringAssert.Contains(message, "Mesh is offline");
        StringAssert.Contains(message, "retry");
        CollectionAssert.AreEqual(
            new[] { "Could not read Protocol 9 capabilities from the relay." },
            diagnostics);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mesh.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Mesh.slnx was not found.");
    }
}
