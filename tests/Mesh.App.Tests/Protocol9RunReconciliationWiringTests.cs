using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class Protocol9RunReconciliationWiringTests
{
    [TestMethod]
    public void CommittedTopicAnswer_ReconcilesQueuedRemoteRun()
    {
        var source = ReadSource("src", "Mesh.App", "Services", "AppState.OnlineReplication.cs");

        StringAssert.Contains(source, "envelope.Action == ReplicationPayloadCodec.DomainAction.AppendLine");
        StringAssert.Contains(source, "line is { Role: \"assistant\" }");
        StringAssert.Contains(source, "ReconcileTopicRunWithAnswer(");
        StringAssert.Contains(source, "line.ReplyToLineId");
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
