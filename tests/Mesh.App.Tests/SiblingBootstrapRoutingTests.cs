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
        StringAssert.Contains(method, "new[] { target.PeerHandle }");
        StringAssert.Contains(method, "Where(static line => !line.Internal)");
        StringAssert.Contains(method, "CloneBootstrapConversation(conversation, includeLines: false)");
        StringAssert.Contains(method, "source.CapturedAt");
        Assert.IsFalse(
            method.Contains("TargetsForConversation", StringComparison.Ordinal),
            "a sibling bootstrap must never fan conversation history out to conversation peers");
        Assert.IsFalse(
            method.Contains("EmitLineUpsert(\"message.bootstrap\"", StringComparison.Ordinal),
            "the generic message helper includes the peer handle and is unsafe for sibling bootstrap");
        var boundary = method.IndexOf("WithProjectionBoundaryAsync", StringComparison.Ordinal);
        var markerWrite = method.IndexOf("ExecuteJournalWrite(() =>", StringComparison.Ordinal);
        var chunkEmission = method.IndexOf("EmitBootstrapChunkAsync", StringComparison.Ordinal);
        Assert.IsTrue(boundary >= 0 && markerWrite > boundary && chunkEmission > markerWrite,
            "capture must persist its cursor-bounded plan before chunk emission");
        var boundaryCallEnd = method.IndexOf(
            "ct).ConfigureAwait(false);",
            boundary,
            StringComparison.Ordinal);
        Assert.IsTrue(boundaryCallEnd > markerWrite && boundaryCallEnd < chunkEmission,
            "the durable plan must be committed before releasing the projection boundary");
        StringAssert.Contains(method, "EnterLocalOriginJournalLock");
        StringAssert.Contains(method, "captureCursor");
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
