using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class SiblingBootstrapRoutingTests
{
    [TestMethod]
    public void ConversationHistoryBootstrap_TargetsOwnerDevicesOnly()
    {
        var source = ReadSource("src", "Mesh.App", "Services", "AppState.OnlineReplication.cs");
        var methodStart = source.IndexOf(
            "public async Task EmitOwnerBootstrapSnapshotAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "public bool HasDueOutbox",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        StringAssert.Contains(method, "ReplicationOpKinds.Message");
        StringAssert.Contains(method, "TargetsForOwnerState()");
        Assert.IsFalse(
            method.Contains("TargetsForConversation", StringComparison.Ordinal),
            "a sibling bootstrap must never fan conversation history out to conversation peers");
        Assert.IsFalse(
            method.Contains("EmitLineUpsert(\"message.bootstrap\"", StringComparison.Ordinal),
            "the generic message helper includes the peer handle and is unsafe for sibling bootstrap");
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
