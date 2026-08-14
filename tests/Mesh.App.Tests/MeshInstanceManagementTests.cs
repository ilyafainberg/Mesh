using System.Text.Json;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MeshInstanceManagementTests
{
    private string root = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), $"mesh-instance-tests-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        MeshProcessContext.Configure(null, null, isHeadless: false);
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [TestMethod]
    public void CommandLine_ParsesRunListCloseAndForwardedActivation()
    {
        var run = MeshInstanceCommand.Parse([
            "Mesh.App.exe", "--handle", "@Alice", "--headless", "--ui-mode", "phone", "mesh://messages/bob"]);
        Assert.AreEqual(MeshInstanceCommandKind.Run, run.Kind);
        Assert.AreEqual("@Alice", run.Handle);
        Assert.IsTrue(run.HandleWasExplicit);
        Assert.IsTrue(run.Headless);
        CollectionAssert.AreEqual(
            new[] { "--ui-mode", "phone", "mesh://messages/bob" },
            run.ForwardedArguments.ToArray());

        var list = MeshInstanceCommand.Parse(["Mesh.App.exe", "--list", "--json"]);
        Assert.AreEqual(MeshInstanceCommandKind.List, list.Kind);
        Assert.IsTrue(list.Json);

        var close = MeshInstanceCommand.Parse(["Mesh.App.exe", "--close", "bob"]);
        Assert.AreEqual(MeshInstanceCommandKind.Close, close.Kind);
        Assert.AreEqual("bob", close.Handle);
    }

    [DataTestMethod]
    [DataRow("@Alice", "alice")]
    [DataRow(" ALICE ", "alice")]
    [DataRow("alice", "alice")]
    public void HandleNames_AreNormalizedAndStable(string raw, string normalized)
    {
        Assert.AreEqual(normalized, MeshInstanceNames.NormalizeHandle(raw));
        Assert.AreEqual(
            MeshInstanceNames.HandleHash(normalized),
            MeshInstanceNames.HandleHash(MeshInstanceNames.NormalizeHandle(raw)));
        StringAssert.StartsWith(MeshInstanceNames.MutexName(normalized), @"Local\Mesh.Handle.");
        StringAssert.StartsWith(MeshInstanceNames.PipeName(normalized), "Mesh.");
    }

    [TestMethod]
    public void AccountIndex_ResolvesExplicitHandleAndDefaultActiveId()
    {
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "accounts.json");
        File.WriteAllText(path, """
            {
              "activeId": "id-bob",
              "accounts": [
                { "id": "id-alice", "handle": "Alice", "displayName": "Alice" },
                { "id": "id-bob", "handle": "@Bob", "displayName": "Bob" }
              ]
            }
            """);

        var snapshot = MeshAccountIndexSnapshot.Load(path);
        Assert.AreEqual("id-alice", snapshot.Resolve("@ALICE")?.Id);
        Assert.AreEqual("alice", snapshot.Resolve("alice")?.NormalizedHandle);
        Assert.AreEqual("id-bob", snapshot.Resolve(null)?.Id);
    }

    [TestMethod]
    public void Registry_ListsLiveInstancesAndRemovesStaleRecords()
    {
        var livePids = new HashSet<int> { 101, 303 };
        var registry = new MeshInstanceRegistry(root, record => livePids.Contains(record.Pid));
        registry.Write(new MeshInstanceRecord("alice", 101, true, DateTimeOffset.UtcNow));
        registry.Write(new MeshInstanceRecord("bob", 202, false, DateTimeOffset.UtcNow));
        registry.Write(new MeshInstanceRecord("carol", 303, false, DateTimeOffset.UtcNow));

        var active = registry.ListActive();

        CollectionAssert.AreEqual(new[] { "alice", "carol" }, active.Select(record => record.Handle).ToArray());
        Assert.IsTrue(active[0].Headless);
        Assert.IsFalse(File.Exists(registry.PathFor("bob")));
    }

    [TestMethod]
    public void Registry_UsesAutomationFriendlyJsonShape()
    {
        var registry = new MeshInstanceRegistry(root, _ => true);
        var started = DateTimeOffset.UtcNow;
        registry.Write(new MeshInstanceRecord("alice", 12345, true, started));

        using var document = JsonDocument.Parse(File.ReadAllText(registry.PathFor("alice")));
        Assert.AreEqual("alice", document.RootElement.GetProperty("handle").GetString());
        Assert.AreEqual(12345, document.RootElement.GetProperty("pid").GetInt32());
        Assert.IsTrue(document.RootElement.GetProperty("headless").GetBoolean());
        Assert.IsTrue(document.RootElement.TryGetProperty("startedAt", out _));
    }

    [TestMethod]
    public void HandleMutex_BlocksSameHandleButAllowsDifferentHandles()
    {
        var firstHandle = "test-" + Guid.NewGuid().ToString("n");
        var secondHandle = "test-" + Guid.NewGuid().ToString("n");
        Assert.IsTrue(MeshHandleMutexLease.TryAcquire(firstHandle, out var first));
        try
        {
            Assert.IsFalse(MeshHandleMutexLease.TryAcquire(firstHandle, out var duplicate));
            Assert.IsNull(duplicate);
            Assert.IsTrue(MeshHandleMutexLease.TryAcquire(secondHandle, out var other));
            other!.Dispose();
        }
        finally
        {
            first!.Dispose();
        }

        Assert.IsTrue(MeshHandleMutexLease.TryAcquire(firstHandle, out var afterCrashEquivalent));
        afterCrashEquivalent!.Dispose();
    }

    [TestMethod]
    public async Task NamedPipe_ActivatesAndShutsDownOnlyItsRandomTestEndpoint()
    {
        var handle = "test-" + Guid.NewGuid().ToString("n");
        var activations = new List<string>();
        await using var server = new MeshInstancePipeServer(
            MeshInstanceNames.PipeName(handle),
            (command, _) =>
            {
                if (command.Command == "activate")
                {
                    activations.AddRange(command.Arguments ?? Array.Empty<string>());
                    return Task.FromResult(new MeshPipeReply("ok"));
                }
                return Task.FromResult(new MeshPipeReply("ok", StopServer: true));
            });

        var activated = await MeshInstancePipeClient.SendAsync(
            handle,
            new MeshPipeCommand("activate", ["mesh://messages/alice"]),
            TimeSpan.FromSeconds(3));
        var closed = await MeshInstancePipeClient.SendAsync(
            handle,
            new MeshPipeCommand("shutdown"),
            TimeSpan.FromSeconds(3));

        Assert.AreEqual("ok", activated);
        Assert.AreEqual("ok", closed);
        CollectionAssert.AreEqual(new[] { "mesh://messages/alice" }, activations);
    }

    [TestMethod]
    public void AccountsWriter_SerializesAndAtomicallyReplacesTheIndex()
    {
        var path = Path.Combine(root, "Data", "accounts.json");
        MeshAccountIndexWriter.WriteAtomic(path, "{\"activeId\":\"first\"}");
        MeshAccountIndexWriter.WriteAtomic(path, "{\"activeId\":\"second\"}");

        Assert.AreEqual("{\"activeId\":\"second\"}", File.ReadAllText(path));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length);
    }

    [TestMethod]
    public async Task LaunchBootstrap_ListsActiveInstancesAsTextAndJson()
    {
        var registry = new MeshInstanceRegistry(root);
        var first = "test-" + Guid.NewGuid().ToString("n");
        var second = "test-" + Guid.NewGuid().ToString("n");
        registry.Write(new MeshInstanceRecord(first, Environment.ProcessId, false, DateTimeOffset.UtcNow));
        registry.Write(new MeshInstanceRecord(second, Environment.ProcessId, true, DateTimeOffset.UtcNow));

        var jsonOutput = new StringWriter();
        var jsonDecision = await MeshDesktopLaunchBootstrap.PrepareAsync(
            ["Mesh.App.exe", "--list", "--json"],
            root,
            Path.Combine(root, "Data"),
            jsonOutput);

        Assert.IsFalse(jsonDecision.ContinueLaunching);
        Assert.AreEqual(0, jsonDecision.ExitCode);
        using (var document = JsonDocument.Parse(jsonOutput.ToString()))
        {
            var instances = document.RootElement.EnumerateArray().ToArray();
            Assert.AreEqual(2, instances.Length);
            CollectionAssert.AreEquivalent(
                new[] { "@" + first, "@" + second },
                instances.Select(item => item.GetProperty("handle").GetString()).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { "Desktop", "Headless" },
                instances.Select(item => item.GetProperty("mode").GetString()).ToArray());
            Assert.IsTrue(instances.All(item => item.GetProperty("pid").GetInt32() == Environment.ProcessId));
            Assert.IsTrue(instances.All(item => item.TryGetProperty("startedAt", out _)));
        }

        var textOutput = new StringWriter();
        var textDecision = await MeshDesktopLaunchBootstrap.PrepareAsync(
            ["Mesh.App.exe", "--list"],
            root,
            Path.Combine(root, "Data"),
            textOutput);

        Assert.AreEqual(0, textDecision.ExitCode);
        StringAssert.Contains(textOutput.ToString(), "HANDLE");
        StringAssert.Contains(textOutput.ToString(), "@" + first);
        StringAssert.Contains(textOutput.ToString(), "@" + second);
    }

    [TestMethod]
    public async Task LaunchBootstrap_CloseRemovesAStaleRandomTestRecord()
    {
        var handle = "test-" + Guid.NewGuid().ToString("n");
        var registry = new MeshInstanceRegistry(root, _ => false);
        registry.Write(new MeshInstanceRecord(handle, int.MaxValue, false, DateTimeOffset.UtcNow));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var output = new StringWriter();

        var decision = await MeshDesktopLaunchBootstrap.PrepareAsync(
            ["Mesh.App.exe", "--close", handle],
            root,
            Path.Combine(root, "Data"),
            output,
            cancelled.Token);

        Assert.IsFalse(decision.ContinueLaunching);
        Assert.AreEqual(1, decision.ExitCode);
        Assert.IsFalse(File.Exists(registry.PathFor(handle)));
        StringAssert.Contains(output.ToString(), "No responsive Mesh instance");
    }

    [TestMethod]
    public async Task HeadlessMode_DeniesApprovalWithoutCreatingPendingUiWork()
    {
        MeshProcessContext.Configure("test-account", "test-handle", isHeadless: true);
        var service = new ToolApprovalService();
        var changed = 0;
        service.Changed += () => changed++;
        using var arguments = JsonDocument.Parse("{\"path\":\"test-only\"}");

        var approved = await service.RequestAsync(
            "write_test_file",
            "Write a test-only file",
            ToolOperationKind.Write,
            arguments.RootElement,
            CancellationToken.None);

        Assert.IsFalse(approved);
        Assert.AreEqual(0, service.Pending.Count);
        Assert.AreEqual(0, changed);
    }

    [TestMethod]
    public async Task AccountSwitch_ReservesTargetBeforeReleasingPreviousHandle()
    {
        var initial = "test-" + Guid.NewGuid().ToString("n");
        var target = "test-" + Guid.NewGuid().ToString("n");
        var conflict = "test-" + Guid.NewGuid().ToString("n");
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.IsTrue(MeshDesktopInstanceRuntime.TryStart(
            root,
            "account-initial",
            initial,
            initial,
            headless: false,
            setup: false));
        MeshDesktopInstanceRuntime.AttachHost(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => exited.TrySetResult(true));

        Assert.IsTrue(MeshHandleMutexLease.TryAcquire(conflict, out var conflictOwner));
        try
        {
            using var denied = MeshDesktopInstanceRuntime.ReserveSwitch("account-conflict", conflict);
            Assert.IsFalse(denied.Acquired);
            Assert.AreEqual(
                "This identity is already running in another Mesh instance.",
                denied.Error);
            Assert.IsFalse(MeshHandleMutexLease.TryAcquire(initial, out var duplicateInitial));
            Assert.IsNull(duplicateInitial);
        }
        finally
        {
            conflictOwner!.Dispose();
        }

        using (var aborted = MeshDesktopInstanceRuntime.ReserveSwitch("account-target", target))
        {
            Assert.IsTrue(aborted.Acquired);
            Assert.IsFalse(MeshHandleMutexLease.TryAcquire(target, out var duplicateTarget));
            Assert.IsNull(duplicateTarget);
            Assert.IsFalse(MeshHandleMutexLease.TryAcquire(initial, out var duplicateInitial));
            Assert.IsNull(duplicateInitial);
            Assert.IsFalse(aborted.Commit(() => false));
        }

        Assert.IsTrue(MeshHandleMutexLease.TryAcquire(target, out var targetAfterAbort));
        targetAfterAbort!.Dispose();
        Assert.IsFalse(MeshHandleMutexLease.TryAcquire(initial, out var initialBeforeCommit));
        Assert.IsNull(initialBeforeCommit);

        using (var reservation = MeshDesktopInstanceRuntime.ReserveSwitch("account-target", target))
        {
            Assert.IsTrue(reservation.Acquired);
            Assert.IsTrue(reservation.Commit(() => true));
        }

        Assert.AreEqual("account-target", MeshProcessContext.PreferredAccountId);
        Assert.AreEqual(target, MeshProcessContext.NormalizedHandle);
        Assert.IsTrue(MeshHandleMutexLease.TryAcquire(initial, out var releasedInitial));
        releasedInitial!.Dispose();
        Assert.IsFalse(MeshHandleMutexLease.TryAcquire(target, out var heldTarget));
        Assert.IsNull(heldTarget);

        Assert.IsTrue(MeshDesktopInstanceRuntime.RequestLocalShutdown());
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(MeshHandleMutexLease.TryAcquire(target, out var targetAfterShutdown));
        targetAfterShutdown!.Dispose();
    }
}
