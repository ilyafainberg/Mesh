using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Relay.Hub;
using Mesh.Relay.LiveFaults;
using Mesh.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.ComponentTests;

[TestClass]
[DoNotParallelize]
public sealed class RelayLiveFaultRuntimeIntegrationTests
{
    private const string AdminKey = "runtime-test-key";
    private readonly List<ClientHarness> createdClients = [];

    [TestCleanup]
    public async Task DisposeClientHarnessesAsync()
    {
        foreach (var harness in createdClients.AsEnumerable().Reverse())
        {
            await harness.Client.DisconnectAsync();
            await harness.State.DisposeAsync();
        }
        createdClients.Clear();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [TestMethod]
    public async Task ActualProgram_EnforcesBuildEnvironmentFlagAndAdminGuards()
    {
        var repository = FindRepositoryRoot();
        var productionAssembly = await EnsureProductionRelayAsync(repository);
        var testAssembly = TestRelayAssembly(repository);

        await using (var production = await RelayProcess.StartAsync(
                         productionAssembly, "Test", enabled: true, AdminKey))
        {
            using var response = await production.Http.GetAsync("/admin/live-faults");
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Console.WriteLine(
                $"PROGRAM_GUARD production assembly={productionAssembly} health=200 admin={(int)response.StatusCode}");
        }

        var refused = await RelayProcess.StartExpectingFailureAsync(
            testAssembly, "Production", enabled: true, AdminKey);
        Assert.AreNotEqual(0, refused.ExitCode);
        StringAssert.Contains(refused.Output, "refuses live-fault activation");
        Console.WriteLine(
            $"PROGRAM_GUARD test-production assembly={testAssembly} exit={refused.ExitCode}");

        await using (var disabled = await RelayProcess.StartAsync(
                         testAssembly, "Test", enabled: false, AdminKey))
        {
            using var response = await disabled.Http.GetAsync("/admin/live-faults");
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Console.WriteLine($"PROGRAM_GUARD test-disabled health=200 admin={(int)response.StatusCode}");
        }

        await using (var enabled = await RelayProcess.StartAsync(
                         testAssembly, "Test", enabled: true, AdminKey))
        {
            using var unauthorized = await enabled.Http.GetAsync("/admin/live-faults");
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var authorized = AdminRequest(HttpMethod.Get, "/admin/live-faults");
            using var response = await enabled.Http.SendAsync(authorized);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Console.WriteLine(
                $"PROGRAM_GUARD test-enabled health=200 unauth={(int)unauthorized.StatusCode} auth={(int)response.StatusCode}");
        }
    }

    [TestMethod]
    public async Task ActualProgram_RealClients_CoalesceRetriesAndWakeDrainExactlyOnce()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(
            repository, "_artifacts", "elvi-hooks", "real-client-state",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var model = await DeterministicModelServer.StartAsync();
        await using var relay = await RelayProcess.StartAsync(
            TestRelayAssembly(repository), "Test", enabled: true, AdminKey);
        var logs = new List<string>();
        ClientHarness? alice = null;
        ClientHarness? bob = null;
        try
        {
            alice = CreateClient(
                "account", "Alice device", relay.BaseUrl, model.BaseUrl, root, "alice", clock);
            bob = CreateClient(
                "account", "Bob device", relay.BaseUrl, model.BaseUrl, root, "bob");
            alice.Client.Log += message =>
            {
                lock (logs) logs.Add("alice: " + message);
                Console.WriteLine("alice: " + message);
            };
            bob.Client.Log += message =>
            {
                lock (logs) logs.Add("bob: " + message);
                Console.WriteLine("bob: " + message);
            };
            await RegisterLinkedDevicesAsync(relay.Http, alice.State, bob.State);
            await bob.Client.ConnectAsync();
            await alice.Client.ConnectAsync();
            await EventuallyAsync(
                () => alice.Client.Connected && bob.Client.Connected,
                TimeSpan.FromSeconds(20));
            await EventuallyAsync(
                () => alice.Client.IsReplicationRosterDeviceAvailable(
                    "account", bob.Client.MyDeviceId),
                TimeSpan.FromSeconds(15));
            var initialBobHandshake = (await RuntimeAsync(relay.Http)).Handshakes.Last(item =>
                item.Stage == "authenticate"
                && item.DeviceId == bob.Client.MyDeviceId
                && item.Accepted == true);
            Assert.IsNotNull(initialBobHandshake.Nonce);

            var triggerAt = DateTimeOffset.UtcNow;
            var thread = alice.State.NewOwnThread(
                "Actual Program runtime",
                new ExecutionDevice(
                    bob.Client.MyDeviceId,
                    "Bob device",
                    DevicePlatforms.Windows),
                createdAt: triggerAt);
            var threadId = thread.Id;
            alice.State.AddOwnChatLine(
                threadId,
                new ChatLine
                {
                    Id = "line-real-client",
                    Role = "user",
                    Text = "execute through the actual relay once",
                    At = triggerAt
                });
            alice.State.RegisterExpectedRemoteRun(
                threadId,
                "run-actual-program-m08",
                new ExecutionDevice(
                    bob.Client.MyDeviceId,
                    "Bob device",
                    DevicePlatforms.Windows),
                triggerAt);
            await EventuallyAsync(
                () => bob.State.Profile.OwnThreads.SingleOrDefault(
                          item => item.Id == threadId) is
                      {
                          ExecutionRunId: "run-actual-program-m08"
                      },
                TimeSpan.FromSeconds(15));

            const string runId = "run-actual-program-m08";
            var terminalId = TopicControlProtocol.EnvelopeId("topic.terminal", runId);
            var activation = new LiveFaultActivationRequest(
                "actual-terminal-success-drop",
                LiveFaultMode.SuccessDropBeforeDestination,
                LiveFaultDirection.Outbound,
                "account",
                alice.Client.MyDeviceId,
                120,
                SourceDevice: bob.Client.MyDeviceId,
                TargetAccount: "account",
                Kind: MeshKinds.TopicRunUpdate,
                StableIdHash: LiveFaultIds.Hash(terminalId));
            var activated = await ActivateAsync(relay.Http, activation);
            Assert.IsTrue(activated.Active);

            var dispatch = await alice.Client.DispatchAsync(
                bob.Client.MyDeviceId,
                new TopicRunRequestPayload(
                    runId,
                    threadId,
                    "line-real-client",
                    "account",
                    "execute through the actual relay once",
                    triggerAt,
                    bob.Client.MyDeviceId,
                    TopicTurnMode.Single),
                [],
                CancellationToken.None);
            Assert.IsTrue(dispatch.Accepted, dispatch.Error);
            await EventuallyAsync(
                () => model.CallCount == 1
                      && alice.State.GetTopicOutbox(runId) is null
                      && bob.State.GetDeviceEnvelopeOutbox(terminalId) is null
                      && bob.State.ListInboundTopicRuns().SingleOrDefault(
                          item => item.RunId == runId)?.State
                      == InboundTopicRunStates.Completed
                      && bob.State.Profile.OwnThreads.SingleOrDefault(
                          item => item.Id == threadId)?.Lines.Count(
                          item => item.Role == "assistant") == 1,
                TimeSpan.FromSeconds(25));

            var m08Runtime = await RuntimeAsync(relay.Http);
            var terminalAttempts = AttemptsFor(m08Runtime, terminalId);
            Console.WriteLine(
                $"M08_ATTEMPTS id={terminalId} hash={LiveFaultIds.Hash(terminalId)} " +
                $"terminal={string.Join(',', terminalAttempts)}");
            CollectionAssert.AreEqual(new[] { 1, 2 }, terminalAttempts);
            Assert.AreEqual(1, model.CallCount);
            Assert.AreEqual(
                1, bob.State.ListInboundTopicRuns().Count(item => item.RunId == runId));
            Assert.AreEqual(
                1,
                bob.State.Profile.OwnThreads.Single(item => item.Id == threadId)
                    .Lines.Count(item => item.Role == "assistant"));

            await bob.Client.DisconnectAsync();
            await EventuallyAsync(
                () => !bob.Client.Connected,
                TimeSpan.FromSeconds(10));
            await EventuallyAsync(
                async () => !(await DevicesAsync(relay.Http, "account"))
                    .Single(device => device.DeviceId == bob.Client.MyDeviceId).Online,
                TimeSpan.FromSeconds(10));

            const string recoveryRunId = "run-actual-program-wake";
            const string recoveryLineId = "line-actual-program-wake";
            var recoveryTerminalId =
                TopicControlProtocol.EnvelopeId("topic.terminal", recoveryRunId);
            var recoveryAt = DateTimeOffset.UtcNow;
            alice.State.AddOwnChatLine(
                threadId,
                new ChatLine
                {
                    Id = recoveryLineId,
                    Role = "user",
                    Text = "wake and drain exactly once",
                    At = recoveryAt
                });
            alice.State.RegisterExpectedRemoteRun(
                threadId,
                recoveryRunId,
                new ExecutionDevice(
                    bob.Client.MyDeviceId,
                    "Bob device",
                    DevicePlatforms.Windows),
                recoveryAt);
            var queued = await alice.Client.DispatchAsync(
                bob.Client.MyDeviceId,
                new TopicRunRequestPayload(
                    recoveryRunId,
                    threadId,
                    recoveryLineId,
                    "account",
                    "wake and drain exactly once",
                    recoveryAt,
                    bob.Client.MyDeviceId,
                    TopicTurnMode.Single),
                [],
                CancellationToken.None);
            Assert.IsTrue(queued.Accepted, queued.Error);
            Assert.IsNotNull(alice.State.GetTopicOutbox(recoveryRunId));
            CollectionAssert.AreEqual(
                new[] { 1 }, AttemptsFor(await RuntimeAsync(relay.Http), recoveryRunId));

            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.IsFalse(
                alice.Client.IsReplicationRosterDeviceAvailable(
                    "account", bob.Client.MyDeviceId),
                "the connected client's real relay roster must evict Bob after its injected expiry");

            await bob.Client.ConnectAsync();
            await EventuallyAsync(
                () => bob.Client.Connected,
                TimeSpan.FromSeconds(15));
            await EventuallyAsync(
                () => model.CallCount == 2
                      && alice.State.GetTopicOutbox(recoveryRunId) is null
                      && bob.State.GetDeviceEnvelopeOutbox(recoveryTerminalId) is null
                      && bob.State.ListInboundTopicRuns()
                          .Count(item => item.RunId == recoveryRunId
                                         && item.State == InboundTopicRunStates.Completed) == 1
                      && bob.State.Profile.OwnThreads.Single(
                          item => item.Id == threadId).Lines.Count(
                          item => item.Role == "assistant") == 2,
                TimeSpan.FromSeconds(20));

            var recoveredRuntime = await RuntimeAsync(relay.Http);
            var recoveredBobHandshake = recoveredRuntime.Handshakes.Last(item =>
                item.Stage == "authenticate"
                && item.DeviceId == bob.Client.MyDeviceId
                && item.Accepted == true);
            Assert.IsNotNull(recoveredBobHandshake.Nonce);
            Assert.AreNotEqual(initialBobHandshake.Nonce, recoveredBobHandshake.Nonce);
            CollectionAssert.AreEqual(
                new[] { 1, 2 }, AttemptsFor(recoveredRuntime, recoveryRunId));
            CollectionAssert.AreEqual(
                new[] { 1 },
                AttemptsFor(
                    recoveredRuntime,
                    TopicControlProtocol.EnvelopeId("topic.accepted", recoveryRunId)));
            CollectionAssert.AreEqual(
                new[] { 1 }, AttemptsFor(recoveredRuntime, recoveryTerminalId));
            CollectionAssert.AreEqual(
                new[] { 1 },
                AttemptsFor(
                    recoveredRuntime,
                    TopicControlProtocol.EnvelopeId("topic.accepted-receipt", recoveryRunId)));
            CollectionAssert.AreEqual(
                new[] { 1 },
                AttemptsFor(
                    recoveredRuntime,
                    TopicControlProtocol.EnvelopeId("topic.terminal-receipt", recoveryRunId)));
            Assert.AreEqual(2, model.CallCount);
            Assert.AreEqual(
                2,
                bob.State.Profile.OwnThreads.Single(item => item.Id == threadId)
                    .Lines.Count(item => item.Role == "assistant"));
            Assert.IsNotNull(alice.State.GetReceivedTopicControl(
                TopicControlProtocol.EnvelopeId("topic.accepted", recoveryRunId)));
            Assert.IsNotNull(alice.State.GetReceivedTopicControl(recoveryTerminalId));
            Assert.AreEqual(0, alice.State.ListTopicOutbox().Count);
            Assert.AreEqual(0, alice.State.ListDeviceEnvelopeOutbox().Count);
            Assert.AreEqual(0, bob.State.ListTopicOutbox().Count);
            Assert.AreEqual(0, bob.State.ListDeviceEnvelopeOutbox().Count);
            lock (logs)
                Assert.AreEqual(
                    0,
                    logs.Count(message => message.Contains(
                        "stale-send-completion", StringComparison.Ordinal)));

            await ClearAsync(relay.Http, activation.RuleId);
            var cleared = await GetRuleAsync(relay.Http, activation.RuleId);
            Assert.IsFalse(cleared.Active);
            var audit = await AuditAsync(relay.Http);
            CollectionAssert.AreEqual(
                new[] { "activated", "consumed" },
                audit.Where(item => item.RuleId == activation.RuleId)
                    .Select(item => item.Event).ToArray());
            Console.WriteLine(
                $"ACTUAL_PROGRAM_EVIDENCE terminalAttempts={string.Join(',', terminalAttempts)} " +
                $"wakeAttempts={string.Join(',', AttemptsFor(recoveredRuntime, recoveryRunId))} " +
                $"rosterExpired=true freshNonce=true modelCalls={model.CallCount} " +
                $"inboundRows=2 assistantRows=2 receipts=2 outboxes=0 staleCompletions=0 duplicates=0");
        }
        finally
        {
            if (alice is not null) await alice.Client.DisconnectAsync();
            if (bob is not null) await bob.Client.DisconnectAsync();
            if (alice?.State.ActiveAccountId is { } aliceId) alice.State.DeleteAccount(aliceId);
            if (bob?.State.ActiveAccountId is { } bobId) bob.State.DeleteAccount(bobId);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task ActualProgram_TerminalAnswerRestartAndLateControlSoakConverge()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(
            repository, "_artifacts", "drummer-terminal", "hosted-state",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var senderClock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var receiverClock = new ManualTimeProvider(senderClock.GetUtcNow());
        await using var model = await DeterministicModelServer.StartAsync();
        await using var relay = await RelayProcess.StartAsync(
            TestRelayAssembly(repository), "Test", enabled: true, AdminKey);
        ClientHarness? sender = null;
        ClientHarness? receiver = null;
        ClientHarness? restartedSender = null;
        ClientHarness? restartedReceiver = null;
        try
        {
            const string runId = "run-terminal-restart-soak";
            const string triggerLineId = "line-terminal-restart-soak";
            sender = CreateClient(
                "terminal-restart", "Sender", relay.BaseUrl, model.BaseUrl,
                root, "sender", senderClock);
            receiver = CreateClient(
                "terminal-restart", "Receiver", relay.BaseUrl, model.BaseUrl,
                root, "receiver", receiverClock);
            var terminalRelayAccepted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            sender.Client.Log += message => Console.WriteLine("terminal-sender: " + message);
            receiver.Client.Log += message =>
            {
                Console.WriteLine("terminal-receiver: " + message);
                if (message.EndsWith(
                        TopicControlProtocol.EnvelopeId("topic.terminal", runId),
                        StringComparison.Ordinal))
                    terminalRelayAccepted.TrySetResult(true);
            };
            await RegisterLinkedDevicesAsync(relay.Http, sender.State, receiver.State);
            await receiver.Client.ConnectAsync();
            await sender.Client.ConnectAsync();
            await EventuallyAsync(
                () => sender.Client.Connected && receiver.Client.Connected,
                TimeSpan.FromSeconds(20));
            await EventuallyAsync(
                () => sender.Client.IsReplicationRosterDeviceAvailable(
                          "terminal-restart", receiver.Client.MyDeviceId)
                      && receiver.Client.IsReplicationRosterDeviceAvailable(
                          "terminal-restart", sender.Client.MyDeviceId),
                TimeSpan.FromSeconds(15));

            var at = senderClock.GetUtcNow();
            var thread = sender.State.NewOwnThread(
                "Terminal restart soak",
                new ExecutionDevice(
                    receiver.Client.MyDeviceId, "Receiver", DevicePlatforms.Windows),
                createdAt: at);
            sender.State.AddOwnChatLine(thread.Id, new ChatLine
            {
                Id = triggerLineId,
                Role = "user",
                Text = "complete while terminal delivery is delayed",
                At = at
            });
            sender.State.RegisterExpectedRemoteRun(
                thread.Id,
                runId,
                new ExecutionDevice(
                    receiver.Client.MyDeviceId, "Receiver", DevicePlatforms.Windows),
                at);
            await EventuallyAsync(
                () => receiver.State.Profile.OwnThreads.Any(item =>
                    item.Id == thread.Id && item.ExecutionRunId == runId),
                TimeSpan.FromSeconds(15));

            var terminalId = TopicControlProtocol.EnvelopeId("topic.terminal", runId);
            var fault = await ActivateAsync(relay.Http, new LiveFaultActivationRequest(
                "terminal-until-both-restart",
                LiveFaultMode.SuccessDropBeforeDestination,
                LiveFaultDirection.Outbound,
                "terminal-restart",
                sender.Client.MyDeviceId,
                120,
                MaxUses: 1000,
                SourceDevice: receiver.Client.MyDeviceId,
                TargetAccount: "terminal-restart",
                Kind: MeshKinds.TopicRunUpdate,
                StableIdHash: LiveFaultIds.Hash(terminalId)));
            Assert.IsTrue(fault.Active);

            var dispatch = await sender.Client.DispatchAsync(
                receiver.Client.MyDeviceId,
                new TopicRunRequestPayload(
                    runId, thread.Id, triggerLineId, "terminal-restart",
                    "complete while terminal delivery is delayed", at,
                    receiver.Client.MyDeviceId, TopicTurnMode.Single),
                [],
                CancellationToken.None);
            Assert.IsTrue(dispatch.Accepted, dispatch.Error);
            await model.FirstResponseCompleted.WaitAsync(TimeSpan.FromSeconds(25));
            await EventuallyAsync(
                () => receiver.State.GetDeviceEnvelopeOutbox(terminalId) is not null,
                TimeSpan.FromSeconds(25),
                () =>
                    $"terminalOutbox=false senderTerminal={sender.State.GetReceivedTopicControl(terminalId) is not null} " +
                    $"assistantRows={sender.State.Profile.OwnThreads.Single(item => item.Id == thread.Id).Lines.Count(line => line.Role == "assistant")} " +
                    $"receiverAssistantRows={receiver.State.Profile.OwnThreads.Single(item => item.Id == thread.Id).Lines.Count(line => line.Role == "assistant")} " +
                    $"topicOutbox={sender.State.GetTopicOutbox(runId) is not null} " +
                    $"inboundState={receiver.State.ListInboundTopicRuns().SingleOrDefault(item => item.RunId == runId)?.State ?? "missing"} " +
                    $"receiverOutboxes={receiver.State.ListDeviceEnvelopeOutbox().Count} modelCalls={model.CallCount} " +
                    $"draft={receiver.State.AssistantDraftFor(thread.Id) is not null} " +
                    $"steps={receiver.State.AgentStepsFor(thread.Id).Count} busy={receiver.State.IsThreadBusy(thread.Id)} " +
                    $"persistenceError={receiver.State.LastPersistenceError ?? "none"}");
            await terminalRelayAccepted.Task.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.IsTrue(
                AttemptsFor(await RuntimeAsync(relay.Http), terminalId).Length >= 1,
                "The active fault must consume the first terminal delivery attempt.");
            await EventuallyAsync(
                () => receiver.State.Profile.OwnThreads.Single(item => item.Id == thread.Id)
                          .Lines.Count(line => line.Role == "assistant") == 1
                      && receiver.State.ListInboundTopicRuns()
                          .Single(item => item.RunId == runId).State
                          == InboundTopicRunStates.Completed,
                TimeSpan.FromSeconds(25),
                () =>
                    $"executorAssistantRows={receiver.State.Profile.OwnThreads.Single(item => item.Id == thread.Id).Lines.Count(line => line.Role == "assistant")} " +
                    $"inboundState={receiver.State.ListInboundTopicRuns().Single(item => item.RunId == runId).State} " +
                    $"requesterTopicOutbox={sender.State.GetTopicOutbox(runId) is not null} modelCalls={model.CallCount}");
            var committedResult = receiver.State.Profile.OwnThreads.Single(
                    item => item.Id == thread.Id)
                .Lines.Single(line => line.Role == "assistant");
            Assert.IsNull(sender.State.GetReceivedTopicControl(terminalId));
            Assert.IsNotNull(receiver.State.GetDeviceEnvelopeOutbox(terminalId));

            var senderSecrets = sender.Secrets;
            var receiverSecrets = receiver.Secrets;
            var senderAccountId = sender.State.ActiveAccountId!;
            var receiverAccountId = receiver.State.ActiveAccountId!;
            var receiverDeviceId = receiver.Client.MyDeviceId;
            await sender.State.FlushPersistenceAsync();
            await receiver.State.FlushPersistenceAsync();
            await sender.Client.DisconnectAsync();
            await receiver.Client.DisconnectAsync();
            sender.State.SignOut();
            receiver.State.SignOut();
            await ClearAsync(relay.Http, fault.RuleId);
            senderClock.Advance(TimeSpan.FromSeconds(60));
            receiverClock.Advance(TimeSpan.FromSeconds(60));

            restartedSender = CreateClient(
                "terminal-restart", "Sender", relay.BaseUrl, model.BaseUrl,
                root, "sender", senderClock, senderSecrets, initializeIdentity: false);
            restartedReceiver = CreateClient(
                "terminal-restart", "Receiver", relay.BaseUrl, model.BaseUrl,
                root, "receiver", receiverClock, receiverSecrets, initializeIdentity: false);
            Assert.IsTrue(restartedSender.State.SwitchAccount(senderAccountId));
            Assert.IsTrue(restartedReceiver.State.SwitchAccount(receiverAccountId));
            await restartedReceiver.Client.ConnectAsync();
            await EventuallyAsync(
                () => restartedReceiver.Client.Connected,
                TimeSpan.FromSeconds(15));
            await restartedSender.Client.ConnectAsync();
            await EventuallyAsync(
                () => restartedSender.Client.IsReplicationRosterDeviceAvailable(
                          "terminal-restart", receiverDeviceId)
                      && restartedReceiver.Client.IsReplicationRosterDeviceAvailable(
                          "terminal-restart", restartedSender.Client.MyDeviceId),
                TimeSpan.FromSeconds(15));
            receiverClock.Advance(TopicTransportPolicy.RemoteAcceptanceRetryInterval);
            restartedReceiver.Client.ResumeTransport();
            await EventuallyAsync(
                () => restartedSender.State.GetReceivedTopicControl(terminalId) is not null
                      && restartedReceiver.State.GetDeviceEnvelopeOutbox(terminalId) is null,
                TimeSpan.FromSeconds(25));

            var terminal = restartedSender.State.GetReceivedTopicControl(terminalId)!;
            Assert.IsTrue(TopicRunProtocol.TryParseUpdate(terminal.UpdateJson, out var terminalUpdate));
            Assert.AreEqual(triggerLineId, terminalUpdate.TriggerLineId);
            Assert.IsNotNull(terminalUpdate.Result);
            Assert.AreEqual(committedResult.Id, terminalUpdate.Result.LineId);
            Assert.AreEqual(committedResult.Text, terminalUpdate.Result.Text);
            Assert.IsTrue(restartedSender.State.Profile.OwnThreads.Single(
                    item => item.Id == thread.Id)
                .Lines.Any(line => line.Id == committedResult.Id));

            const string faultRunId = "run-generated-wrong-trigger";
            const string faultLineId = "line-generated-wrong-trigger";
            var scheduler = new GeneratedTopicFaultScheduler(
                faultRunId, "line-conflicting-trigger");
            restartedReceiver.Client.TopicEnvelopeTestFaultScheduler = scheduler;
            var faultAt = receiverClock.GetUtcNow().AddMinutes(1);
            var faultDispatch = await new TopicExecutionRouter(
                    restartedSender.State,
                    restartedSender.Runner,
                    restartedSender.Client)
                .SubmitAsync(
                    new TopicTurnDraft(
                        faultRunId,
                        thread.Id,
                        faultLineId,
                        "terminal-restart",
                        "generate, reject, and retry the real terminal control",
                        faultAt,
                        TopicTurnMode.Single,
                        receiverDeviceId),
                    null,
                    CancellationToken.None);
            Assert.IsTrue(faultDispatch.Accepted, faultDispatch.Error);
            await EventuallyAsync(
                () => scheduler.MutatedTerminalCount == 1
                      && scheduler.DelayedAcceptedCount >= 1
                      && scheduler.DelayedRunningCount >= 1
                      && restartedReceiver.State.GetDeviceEnvelopeOutbox(
                          TopicControlProtocol.EnvelopeId("topic.terminal", faultRunId)) is not null
                      && restartedSender.State.GetReceivedTopicControl(
                          TopicControlProtocol.EnvelopeId("topic.terminal", faultRunId)) is null,
                TimeSpan.FromSeconds(25));

            senderClock.Advance(TimeSpan.FromSeconds(60));
            receiverClock.Advance(TimeSpan.FromSeconds(60));
            await scheduler.ReleaseAsync();
            restartedReceiver.Client.ResumeTransport();
            await EventuallyStableAsync(
                () => restartedSender.State.GetReceivedTopicControl(
                          TopicControlProtocol.EnvelopeId("topic.terminal", faultRunId)) is not null
                      && restartedReceiver.State.GetDeviceEnvelopeOutbox(
                          TopicControlProtocol.EnvelopeId("topic.terminal", faultRunId)) is null
                      && restartedSender.State.ListTopicOutbox().Count == 0
                      && restartedSender.State.ListDeviceEnvelopeOutbox().Count == 0
                      && restartedReceiver.State.ListTopicOutbox().Count == 0
                      && restartedReceiver.State.ListDeviceEnvelopeOutbox().Count == 0,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(25));

            var finalThread = restartedSender.State.Profile.OwnThreads.Single(
                item => item.Id == thread.Id);
            Assert.IsNull(finalThread.ExecutionRunId);
            Assert.AreEqual(2, finalThread.Lines.Count(line => line.Role == "assistant"));
            var generatedTerminal = restartedSender.State.GetReceivedTopicControl(
                TopicControlProtocol.EnvelopeId("topic.terminal", faultRunId));
            Assert.IsNotNull(generatedTerminal);
            Assert.IsTrue(TopicRunProtocol.TryParseUpdate(
                generatedTerminal.UpdateJson, out var generatedTerminalUpdate));
            Assert.AreEqual(faultLineId, generatedTerminalUpdate.TriggerLineId);
            Assert.AreEqual(1, scheduler.MutatedTerminalCount);
            Assert.IsTrue(scheduler.CorrectTerminalCount >= 1);
            Assert.AreEqual(2, model.CallCount);
            Assert.IsTrue(restartedSender.State.IsRetainedTopicRunCorrelation(
                runId, thread.Id, receiverDeviceId));
            Assert.AreEqual(
                InboundTopicRunStates.Completed,
                restartedReceiver.State.ListInboundTopicRuns()
                    .Single(item => item.RunId == runId).State);
            Assert.AreEqual(0, restartedSender.State.ListTopicOutbox().Count);
            Assert.AreEqual(0, restartedSender.State.ListDeviceEnvelopeOutbox().Count);
            Assert.AreEqual(0, restartedReceiver.State.ListTopicOutbox().Count);
            Assert.AreEqual(0, restartedReceiver.State.ListDeviceEnvelopeOutbox().Count);
            Console.WriteLine(
                "DRUMMER_TERMINAL hostedProfiles=2 restarts=2 virtualSoakSeconds=60 " +
                "productionPrompts=2 runnerInvocations=2 durableAnswerBeforeRestart=true " +
                "wrongTriggerRejected=true generatedRetry=true lateRunningAccepted=true " +
                "terminalFinal=true outboxes=0 retainedCorrelation=true");
        }
        finally
        {
            if (sender is not null) await sender.Client.DisconnectAsync();
            if (receiver is not null) await receiver.Client.DisconnectAsync();
            if (restartedSender is not null) await restartedSender.Client.DisconnectAsync();
            if (restartedReceiver is not null) await restartedReceiver.Client.DisconnectAsync();
            sender?.State.SignOut();
            receiver?.State.SignOut();
            restartedSender?.State.SignOut();
            restartedReceiver?.State.SignOut();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task ActualProgram_DualClients_ReconcileGeneratedOrdersOlderAnswersAndLegacyFence()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(
            repository, "_artifacts", "monica-terminal", "hosted-state",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await using var model = await DeterministicModelServer.StartAsync();
        await using var relay = await RelayProcess.StartAsync(
            TestRelayAssembly(repository), "Test", enabled: true, AdminKey);
        ClientHarness? sender = null;
        ClientHarness? receiver = null;
        try
        {
            sender = CreateClient(
                "terminal-dual", "Sender", relay.BaseUrl, model.BaseUrl, root, "sender");
            receiver = CreateClient(
                "terminal-dual", "Receiver", relay.BaseUrl, model.BaseUrl, root, "receiver");
            var clientLogs = new ConcurrentQueue<string>();
            void RecordClientLog(string owner, string message)
            {
                clientLogs.Enqueue(owner + ":" + message);
                while (clientLogs.Count > 24) clientLogs.TryDequeue(out _);
            }
            sender.Client.Log += message => RecordClientLog("sender", message);
            receiver.Client.Log += message => RecordClientLog("receiver", message);
            string StageDiagnostics(string stage)
            {
                var senderEngine = sender.Client.OnlineReplicationEngine;
                var receiverEngine = receiver.Client.OnlineReplicationEngine;
                static int Count(Func<int> read)
                {
                    try { return read(); }
                    catch { return -1; }
                }

                return
                    $"stage={stage} relayPid={relay.ProcessId} relayPort={relay.Port} " +
                    $"relayExited={relay.HasExited} modelPid={model.ProcessId} modelPort={model.Port} " +
                    $"modelCalls={model.CallCount} senderConnected={sender.Client.Connected} " +
                    $"receiverConnected={receiver.Client.Connected} " +
                    $"senderSeesReceiver={sender.Client.IsReplicationRosterDeviceAvailable("terminal-dual", receiver.Client.MyDeviceId)} " +
                    $"receiverSeesSender={receiver.Client.IsReplicationRosterDeviceAvailable("terminal-dual", sender.Client.MyDeviceId)} " +
                    $"senderThreads={Count(() => sender.State.Profile.OwnThreads.Count)} " +
                    $"receiverThreads={Count(() => receiver.State.Profile.OwnThreads.Count)} " +
                    $"senderReplicationPending={Count(() => sender.State.CountPendingReplicationEvents())} " +
                    $"receiverReplicationPending={Count(() => receiver.State.CountPendingReplicationEvents())} " +
                    $"senderSession={senderEngine?.IsSessionEstablished(receiver.Client.MyDeviceId)} " +
                    $"receiverSession={receiverEngine?.IsSessionEstablished(sender.Client.MyDeviceId)} " +
                    $"senderReplicationError={senderEngine?.LastError ?? "<none>"} " +
                    $"receiverReplicationError={receiverEngine?.LastError ?? "<none>"} " +
                    $"senderTopicOutbox={Count(() => sender.State.ListTopicOutbox().Count)} " +
                    $"receiverTopicOutbox={Count(() => receiver.State.ListTopicOutbox().Count)} " +
                    $"senderEnvelopeOutbox={Count(() => sender.State.ListDeviceEnvelopeOutbox().Count)} " +
                    $"receiverEnvelopeOutbox={Count(() => receiver.State.ListDeviceEnvelopeOutbox().Count)} " +
                    $"senderPersistenceError={sender.State.LastPersistenceError ?? "<none>"} " +
                    $"receiverPersistenceError={receiver.State.LastPersistenceError ?? "<none>"} " +
                    $"clientTail={string.Join(" | ", clientLogs)} " +
                    $"relayTail={relay.OutputTail}";
            }

            Task WaitConditionAsync(string stage, Func<bool> condition, TimeSpan timeout)
                => EventuallyAsync(condition, timeout, () => StageDiagnostics(stage));

            async Task WaitSignalAsync(string stage, Task signal, TimeSpan timeout)
            {
                try
                {
                    await signal.WaitAsync(timeout);
                }
                catch (TimeoutException)
                {
                    Assert.Fail("Timed out waiting for the actual Program runtime signal. " +
                                StageDiagnostics(stage));
                }
            }

            await RegisterLinkedDevicesAsync(relay.Http, sender.State, receiver.State);
            await receiver.Client.ConnectAsync();
            await sender.Client.ConnectAsync();
            await WaitConditionAsync(
                "clients-connected",
                () => sender.Client.Connected
                      && receiver.Client.Connected
                      && sender.Client.OnlineReplicationEngine is not null
                      && receiver.Client.OnlineReplicationEngine is not null,
                TimeSpan.FromSeconds(20));
            await WaitConditionAsync(
                "mutual-roster-ready",
                () => sender.Client.IsReplicationRosterDeviceAvailable(
                          "terminal-dual", receiver.Client.MyDeviceId)
                      && receiver.Client.IsReplicationRosterDeviceAvailable(
                          "terminal-dual", sender.Client.MyDeviceId),
                TimeSpan.FromSeconds(15));

            async Task<OwnThread> NewSyncedThreadAsync(string id)
            {
                var created = sender.State.NewOwnThread(
                    id,
                    new ExecutionDevice(
                        receiver.Client.MyDeviceId, "Receiver", DevicePlatforms.Windows),
                    createdAt: DateTimeOffset.UtcNow);
                await WaitConditionAsync(
                    $"thread-synced:{id}",
                    () => receiver.State.Profile.OwnThreads.Any(item => item.Id == created.Id),
                    TimeSpan.FromSeconds(15));
                return created;
            }

            async Task<TopicDispatchResult> SubmitModernAsync(
                OwnThread thread,
                string runId,
                string lineId,
                string prompt)
            {
                var at = DateTimeOffset.UtcNow;
                sender.State.AddOwnChatLine(thread.Id, new ChatLine
                {
                    Id = lineId,
                    Role = "user",
                    SenderHandle = "terminal-dual",
                    Text = prompt,
                    At = at
                });
                await WaitConditionAsync(
                    $"user-line-synced:{runId}",
                    () => receiver.State.Profile.OwnThreads
                        .Single(item => item.Id == thread.Id).Lines
                        .Any(line => line.Id == lineId),
                    TimeSpan.FromSeconds(15));
                return await new TopicExecutionRouter(
                        sender.State, sender.Runner, sender.Client)
                    .SubmitAsync(
                        new TopicTurnDraft(
                            runId, thread.Id, lineId, "terminal-dual", prompt, at,
                            TopicTurnMode.Single, receiver.Client.MyDeviceId),
                        null,
                        CancellationToken.None);
            }

            foreach (var upsertFirst in new[] { false, true })
            {
                var suffix = upsertFirst ? "upsert-first" : "append-first";
                var thread = await NewSyncedThreadAsync("generated-" + suffix);
                var scheduler = new GeneratedReplicationPairScheduler(thread.Id, upsertFirst);
                receiver.State.ReplicationEventTestFaultScheduler = scheduler;
                var runId = "run-" + suffix;
                var lineId = "line-" + suffix;
                var result = await SubmitModernAsync(
                    thread, runId, lineId, "generated " + suffix);
                Assert.IsTrue(result.Accepted, result.Error);
                await WaitSignalAsync(
                    $"generated-pair-captured:{suffix}",
                    scheduler.Captured,
                    TimeSpan.FromSeconds(20));
                scheduler.Release();
                receiver.State.ReplicationEventTestFaultScheduler = null;
                await receiver.State.FlushPersistenceAsync();
                await WaitConditionAsync(
                    $"generated-pair-converged:{suffix}",
                    () => sender.State.Profile.OwnThreads
                              .Single(item => item.Id == thread.Id).Lines
                              .Count(line => line.Role == "assistant"
                                             && line.ReplyToLineId == lineId) == 1
                          && sender.State.Profile.OwnThreads
                              .Single(item => item.Id == thread.Id).ExecutionRunId is null
                          && sender.State.GetTopicOutbox(runId) is null,
                    TimeSpan.FromSeconds(25));
                CollectionAssert.AreEqual(
                    upsertFirst
                        ? new[]
                        {
                            ReplicationPayloadCodec.DomainAction.Upsert,
                            ReplicationPayloadCodec.DomainAction.AppendLine
                        }
                        : new[]
                        {
                            ReplicationPayloadCodec.DomainAction.AppendLine,
                            ReplicationPayloadCodec.DomainAction.Upsert
                        },
                    scheduler.ReleasedOrder.ToArray());
            }

            var sequential = await NewSyncedThreadAsync("sequential-fence");
            var oldDelay = new GeneratedAssistantAppendDelayScheduler(sequential.Id);
            receiver.State.ReplicationEventTestFaultScheduler = oldDelay;
            var oldResult = await SubmitModernAsync(
                sequential, "run-sequential-old", "line-sequential-old", "old prompt");
            Assert.IsTrue(oldResult.Accepted, oldResult.Error);
            await WaitSignalAsync(
                "sequential-old-captured",
                oldDelay.Captured,
                TimeSpan.FromSeconds(20));
            await WaitConditionAsync(
                "sequential-old-terminal",
                () => sender.State.GetTopicOutbox("run-sequential-old") is null,
                TimeSpan.FromSeconds(20));

            model.PauseNextResponse();
            var callsBeforeNew = model.CallCount;
            var newResult = await SubmitModernAsync(
                sequential, "run-sequential-new", "line-sequential-new", "new prompt");
            Assert.IsTrue(newResult.Accepted, newResult.Error);
            await WaitConditionAsync(
                "sequential-new-model-started",
                () => model.CallCount == callsBeforeNew + 1
                      && sender.State.Profile.OwnThreads
                          .Single(item => item.Id == sequential.Id).ExecutionRunId
                      == "run-sequential-new",
                TimeSpan.FromSeconds(20));
            oldDelay.Release();
            receiver.State.ReplicationEventTestFaultScheduler = null;
            await receiver.State.FlushPersistenceAsync();
            await WaitConditionAsync(
                "sequential-old-reply-released",
                () => sender.State.Profile.OwnThreads
                    .Single(item => item.Id == sequential.Id).Lines
                    .Any(line => line.ReplyToLineId == "line-sequential-old"),
                TimeSpan.FromSeconds(20));
            Assert.AreEqual(
                "run-sequential-new",
                sender.State.Profile.OwnThreads
                    .Single(item => item.Id == sequential.Id).ExecutionRunId);
            Assert.IsNotNull(sender.State.GetTopicOutbox("run-sequential-new"));
            model.ReleaseResponse();
            await WaitConditionAsync(
                "sequential-new-converged",
                () => sender.State.GetTopicOutbox("run-sequential-new") is null
                      && sender.State.Profile.OwnThreads
                          .Single(item => item.Id == sequential.Id).ExecutionRunId is null,
                TimeSpan.FromSeconds(25));

            var legacy = await NewSyncedThreadAsync("legacy-no-correlation");
            var legacyAt = DateTimeOffset.UtcNow;
            const string legacyLine = "line-legacy-no-correlation";
            const string legacyRun = "run-legacy-no-correlation";
            sender.State.AddOwnChatLine(legacy.Id, new ChatLine
            {
                Id = legacyLine,
                Role = "user",
                Text = "legacy uncorrelated",
                At = legacyAt
            });
            sender.State.RegisterExpectedRemoteRun(
                legacy.Id,
                legacyRun,
                new ExecutionDevice(
                    receiver.Client.MyDeviceId, "Receiver", DevicePlatforms.Windows),
                legacyAt);
            await WaitConditionAsync(
                "legacy-user-line-synced",
                () => receiver.State.Profile.OwnThreads
                    .Single(item => item.Id == legacy.Id).Lines
                    .Any(line => line.Id == legacyLine),
                TimeSpan.FromSeconds(15));
            receiver.State.LegacyUncorrelatedTopicAnswerTestMode = true;
            var legacyDispatch = await sender.Client.DispatchAsync(
                receiver.Client.MyDeviceId,
                new TopicRunRequestPayload(
                    legacyRun, legacy.Id, legacyLine, "terminal-dual",
                    "legacy uncorrelated", legacyAt, receiver.Client.MyDeviceId,
                    TopicTurnMode.Single),
                [],
                CancellationToken.None);
            Assert.IsTrue(legacyDispatch.Accepted, legacyDispatch.Error);
            await WaitConditionAsync(
                "legacy-uncorrelated-converged",
                () => sender.State.Profile.OwnThreads
                              .Single(item => item.Id == legacy.Id).Lines
                              .Any(line => line.Role == "assistant"
                                           && line.ReplyToLineId is null)
                          && sender.State.Profile.OwnThreads
                              .Single(item => item.Id == legacy.Id).ExecutionRunId is null,
                TimeSpan.FromSeconds(25));

            var legacyFence = await NewSyncedThreadAsync("legacy-active-fence");
            var fencedAt = DateTimeOffset.UtcNow;
            const string fencedOldLine = "line-legacy-fenced-old";
            sender.State.AddOwnChatLine(legacyFence.Id, new ChatLine
            {
                Id = fencedOldLine,
                Role = "user",
                Text = "delayed legacy answer",
                At = fencedAt
            });
            sender.State.RegisterExpectedRemoteRun(
                legacyFence.Id,
                "run-legacy-fenced-old",
                new ExecutionDevice(
                    receiver.Client.MyDeviceId, "Receiver", DevicePlatforms.Windows),
                fencedAt);
            await WaitConditionAsync(
                "legacy-fence-user-line-synced",
                () => receiver.State.Profile.OwnThreads
                    .Single(item => item.Id == legacyFence.Id).Lines
                    .Any(line => line.Id == fencedOldLine),
                TimeSpan.FromSeconds(15));
            var legacyDelay = new GeneratedAssistantAppendDelayScheduler(legacyFence.Id);
            receiver.State.ReplicationEventTestFaultScheduler = legacyDelay;
            var fencedLegacyDispatch = await sender.Client.DispatchAsync(
                receiver.Client.MyDeviceId,
                new TopicRunRequestPayload(
                    "run-legacy-fenced-old", legacyFence.Id, fencedOldLine,
                    "terminal-dual", "delayed legacy answer", fencedAt,
                    receiver.Client.MyDeviceId, TopicTurnMode.Single),
                [],
                CancellationToken.None);
            Assert.IsTrue(fencedLegacyDispatch.Accepted, fencedLegacyDispatch.Error);
            await WaitSignalAsync(
                "legacy-fence-old-captured",
                legacyDelay.Captured,
                TimeSpan.FromSeconds(20));

            receiver.State.LegacyUncorrelatedTopicAnswerTestMode = false;
            model.PauseNextResponse();
            var callsBeforeFence = model.CallCount;
            var fencedNew = await SubmitModernAsync(
                legacyFence,
                "run-legacy-fenced-new",
                "line-legacy-fenced-new",
                "active modern prompt");
            Assert.IsTrue(fencedNew.Accepted, fencedNew.Error);
            await WaitConditionAsync(
                "legacy-fence-new-model-started",
                () => model.CallCount == callsBeforeFence + 1
                      && sender.State.Profile.OwnThreads
                          .Single(item => item.Id == legacyFence.Id).ExecutionRunId
                      == "run-legacy-fenced-new",
                TimeSpan.FromSeconds(20));
            legacyDelay.Release();
            receiver.State.ReplicationEventTestFaultScheduler = null;
            await receiver.State.FlushPersistenceAsync();
            await WaitConditionAsync(
                "legacy-fence-old-reply-released",
                () => sender.State.Profile.OwnThreads
                    .Single(item => item.Id == legacyFence.Id).Lines
                    .Any(line => line.Role == "assistant" && line.ReplyToLineId is null),
                TimeSpan.FromSeconds(20));
            Assert.AreEqual(
                "run-legacy-fenced-new",
                sender.State.Profile.OwnThreads
                    .Single(item => item.Id == legacyFence.Id).ExecutionRunId);
            Assert.IsNotNull(sender.State.GetTopicOutbox("run-legacy-fenced-new"));
            model.ReleaseResponse();
            await WaitConditionAsync(
                "legacy-fence-final-convergence",
                () => sender.State.GetTopicOutbox("run-legacy-fenced-new") is null
                      && sender.State.Profile.OwnThreads
                          .Single(item => item.Id == legacyFence.Id).ExecutionRunId is null
                      && sender.State.ListTopicOutbox().Count == 0
                      && sender.State.ListDeviceEnvelopeOutbox().Count == 0
                      && receiver.State.ListTopicOutbox().Count == 0
                      && receiver.State.ListDeviceEnvelopeOutbox().Count == 0,
                TimeSpan.FromSeconds(25));

            Assert.AreEqual(0, sender.State.ListTopicOutbox().Count);
            Assert.AreEqual(0, sender.State.ListDeviceEnvelopeOutbox().Count);
            Assert.AreEqual(0, receiver.State.ListTopicOutbox().Count);
            Assert.AreEqual(0, receiver.State.ListDeviceEnvelopeOutbox().Count);
            Console.WriteLine(
                "MONICA_TERMINAL hostedProfiles=2 encryptedDatabases=2 " +
                "generatedOrders=2 sequentialPrompts=2 oldReplyFence=true " +
                "legacyUncorrelatedFallback=true legacyActiveFence=true outboxes=0");
        }
        finally
        {
            model.ReleaseResponse();
            if (sender is not null) await sender.Client.DisconnectAsync();
            if (receiver is not null) await receiver.Client.DisconnectAsync();
            sender?.State.SignOut();
            receiver?.State.SignOut();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task ActualProgram_MigratedActiveRun_BindsOnceFromGeneratedControlAfterRestart()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(
            repository, "_artifacts", "monica-terminal", "migrated-hosted-state",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await using var model = await DeterministicModelServer.StartAsync();
        await using var relay = await RelayProcess.StartAsync(
            TestRelayAssembly(repository), "Test", enabled: true, AdminKey);
        ClientHarness? sender = null;
        ClientHarness? receiver = null;
        ClientHarness? restarted = null;
        try
        {
            sender = CreateClient(
                "migrated-active", "Sender", relay.BaseUrl, model.BaseUrl, root, "sender");
            receiver = CreateClient(
                "migrated-active", "Receiver", relay.BaseUrl, model.BaseUrl, root, "receiver");
            await RegisterLinkedDevicesAsync(relay.Http, sender.State, receiver.State);
            await receiver.Client.ConnectAsync();
            await sender.Client.ConnectAsync();
            await EventuallyAsync(
                () => sender.Client.Connected && receiver.Client.Connected,
                TimeSpan.FromSeconds(20));

            var at = DateTimeOffset.UtcNow;
            const string runId = "run-migrated-active-hosted";
            const string lineId = "line-migrated-active-hosted";
            var thread = sender.State.NewOwnThread(
                "Migrated active hosted",
                new ExecutionDevice(
                    receiver.Client.MyDeviceId, "Receiver", DevicePlatforms.Windows),
                createdAt: at);
            sender.State.AddOwnChatLine(thread.Id, new ChatLine
            {
                Id = lineId,
                Role = "user",
                SenderHandle = "migrated-active",
                Text = "bind the migrated run from a generated accepted control",
                At = at
            });
            sender.State.RegisterExpectedRemoteRun(
                thread.Id,
                runId,
                new ExecutionDevice(
                    receiver.Client.MyDeviceId, "Receiver", DevicePlatforms.Windows),
                at);
            await sender.State.FlushPersistenceAsync();
            await EventuallyAsync(
                () => receiver.State.Profile.OwnThreads.Any(item =>
                    item.Id == thread.Id && item.Lines.Any(line => line.Id == lineId)),
                TimeSpan.FromSeconds(15));

            var accountId = sender.State.ActiveAccountId!;
            var databasePath = sender.State.ActiveDatabasePath!;
            var databaseKey = sender.Secrets.GetDbKey(accountId)!;
            var senderSecrets = sender.Secrets;
            var receiverDeviceId = receiver.Client.MyDeviceId;
            await sender.Client.DisconnectAsync();
            sender.State.SignOut();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            CreatePreTriggerActiveCorrelationFixture(
                databasePath, databaseKey, runId, thread.Id, receiverDeviceId, at);

            restarted = CreateClient(
                "migrated-active", "Sender", relay.BaseUrl, model.BaseUrl,
                root, "sender", secrets: senderSecrets, initializeIdentity: false);
            Assert.IsTrue(restarted.State.SwitchAccount(accountId));
            await restarted.Client.ConnectAsync();
            await EventuallyAsync(
                () => restarted.Client.Connected,
                TimeSpan.FromSeconds(15));
            await EventuallyAsync(
                () => restarted.Client.IsReplicationRosterDeviceAvailable(
                    "migrated-active", receiverDeviceId),
                TimeSpan.FromSeconds(15));

            var dispatch = await restarted.Client.DispatchAsync(
                receiverDeviceId,
                new TopicRunRequestPayload(
                    runId, thread.Id, lineId, "migrated-active",
                    "bind the migrated run from a generated accepted control", at,
                    receiverDeviceId, TopicTurnMode.Single),
                [],
                CancellationToken.None);
            Assert.IsTrue(dispatch.Accepted, dispatch.Error);
            await EventuallyAsync(
                () => restarted.State.GetReceivedTopicControl(
                              TopicControlProtocol.EnvelopeId("topic.accepted", runId)) is not null
                      && restarted.State.GetReceivedTopicControl(
                              TopicControlProtocol.EnvelopeId("topic.terminal", runId)) is not null
                      && restarted.State.Profile.OwnThreads
                          .Single(item => item.Id == thread.Id).Lines.Count(line =>
                              line.Role == "assistant" && line.ReplyToLineId == lineId) == 1
                      && restarted.State.IsRetainedTopicRunCorrelation(
                          runId, thread.Id, receiverDeviceId)
                      && receiver.State.ListDeviceEnvelopeOutbox().Count == 0,
                TimeSpan.FromSeconds(25),
                () =>
                {
                    var acceptedId = TopicControlProtocol.EnvelopeId("topic.accepted", runId);
                    var terminalId = TopicControlProtocol.EnvelopeId("topic.terminal", runId);
                    return
                       $"accepted={restarted.State.GetReceivedTopicControl(acceptedId) is not null} " +
                       $"terminal={restarted.State.GetReceivedTopicControl(terminalId) is not null} " +
                       $"assistantRows={restarted.State.Profile.OwnThreads.Single(item => item.Id == thread.Id).Lines.Count(line => line.Role == "assistant" && line.ReplyToLineId == lineId)} " +
                       $"retained={restarted.State.IsRetainedTopicRunCorrelation(runId, thread.Id, receiverDeviceId)} " +
                       $"receiverOutboxes={receiver.State.ListDeviceEnvelopeOutbox().Count} modelCalls={model.CallCount}";
                });
            Assert.AreEqual(1, model.CallCount);
            Assert.AreEqual(0, restarted.State.ListTopicOutbox().Count);
            Assert.AreEqual(0, restarted.State.ListDeviceEnvelopeOutbox().Count);
            Console.WriteLine(
                "MONICA_MIGRATED_HOSTED hostedProfiles=2 restart=1 oldSchema=true " +
                "generatedAccepted=true generatedTerminal=true bindOnce=true assistantRows=1 outboxes=0");
        }
        finally
        {
            if (sender is not null) await sender.Client.DisconnectAsync();
            if (restarted is not null) await restarted.Client.DisconnectAsync();
            if (receiver is not null) await receiver.Client.DisconnectAsync();
            sender?.State.SignOut();
            restarted?.State.SignOut();
            receiver?.State.SignOut();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task ActualProgram_FirstRunTriggerReusesAuthoritativeRunAfterSenderRestart()
        {
            var repository = FindRepositoryRoot();
            var root = Path.Combine(
                repository,
                "_artifacts",
                "monica-first-run",
                "hosted-state",
                Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);
            await using var model = await DeterministicModelServer.StartAsync();
            await using var relay = await RelayProcess.StartAsync(
                TestRelayAssembly(repository), "Test", enabled: true, AdminKey);
            ClientHarness? sender = null;
            ClientHarness? receiver = null;
            ClientHarness? restarted = null;
            try
            {
                sender = CreateClient(
                    "first-run", "Sender", relay.BaseUrl, model.BaseUrl, root, "sender");
                receiver = CreateClient(
                    "first-run", "Receiver", relay.BaseUrl, model.BaseUrl, root, "receiver");
                await RegisterLinkedDevicesAsync(relay.Http, sender.State, receiver.State);
                await receiver.Client.ConnectAsync();
                await sender.Client.ConnectAsync();
                await EventuallyAsync(
                    () => sender.Client.Connected && receiver.Client.Connected,
                    TimeSpan.FromSeconds(20));
                await EventuallyAsync(
                    () => sender.Client.IsReplicationRosterDeviceAvailable(
                        "first-run", receiver.Client.MyDeviceId),
                    TimeSpan.FromSeconds(15));

                var at = DateTimeOffset.UtcNow;
                var thread = sender.State.NewOwnThread(
                    "Durable first run",
                    new ExecutionDevice(
                        receiver.Client.MyDeviceId,
                        "Receiver",
                        DevicePlatforms.Windows),
                    createdAt: at);
                var journal = new InMemoryTopicSendIdentityStore();
                var sends = new TopicSendCoordinator(identityStore: journal);
                var stableSubmission = sends.CreateSnapshot(
                    thread.Id,
                    receiver.Client.MyDeviceId,
                    composerRevision: 1,
                    Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes("execute the durable first run"))),
                    at);
                var router = new TopicExecutionRouter(
                    sender.State, sender.Runner, sender.Client);
                TopicTurnDraft? initial = null;
                TopicDispatchResult? dispatched = null;
                var submission = sends.Submit(
                    stableSubmission,
                    async (snapshot, handoff) =>
                    {
                        Assert.IsTrue(journal.TryGetUnresolved(
                            snapshot.ScopeIdentity,
                            out var persisted));
                        Assert.AreEqual(
                            snapshot.OperationId,
                            persisted!.OperationId);
                        initial = new TopicTurnDraft(
                            snapshot.RunId,
                            thread.Id,
                            snapshot.LineId,
                            "first-run",
                            "execute the durable first run",
                            at,
                            TopicTurnMode.Single,
                            receiver.Client.MyDeviceId,
                            TriggerOperationId: persisted.OperationId);
                        dispatched = await router.SubmitAsync(
                            initial, null, CancellationToken.None);
                        if (dispatched.Durable)
                            handoff.MarkDurableBoundaryEntered();
                        return new TopicSendHandoff(
                            dispatched.Accepted,
                            dispatched.Code,
                            dispatched.Error);
                    });
                Assert.AreEqual(TopicSendSubmissionKind.Started, submission.Kind);
                await EventuallyAsync(
                    () => dispatched is not null,
                    TimeSpan.FromSeconds(10));
                var committedDraft = initial
                    ?? throw new AssertFailedException("The journaled draft was not dispatched.");
                var firstDispatch = dispatched
                    ?? throw new AssertFailedException("The journaled draft had no dispatch result.");

                Assert.IsTrue(firstDispatch.Accepted, firstDispatch.Error);
                Assert.AreEqual(committedDraft.RunId, firstDispatch.RunId);
                await EventuallyAsync(
                    () => model.CallCount == 1
                          && sender.State.GetTopicOutbox(committedDraft.RunId) is null
                          && receiver.State.ListInboundTopicRuns().Count(item =>
                              item.RunId == committedDraft.RunId
                              && item.State == InboundTopicRunStates.Completed) == 1,
                    TimeSpan.FromSeconds(25),
                    () =>
                        $"modelCalls={model.CallCount} " +
                        $"senderOutbox={sender.State.GetTopicOutbox(committedDraft.RunId) is not null} " +
                        $"completedRows={receiver.State.ListInboundTopicRuns().Count(item => item.RunId == committedDraft.RunId && item.State == InboundTopicRunStates.Completed)}");
                Assert.AreEqual(
                    1,
                    receiver.State.Profile.OwnThreads.Single(item => item.Id == thread.Id)
                        .Lines.Count(item => item.Role == "assistant"));
                var requestAttempts = AttemptsFor(
                    await RuntimeAsync(relay.Http), committedDraft.RunId);
                Assert.IsTrue(requestAttempts.Length >= 1);
                CollectionAssert.AreEqual(
                    Enumerable.Range(1, requestAttempts.Length).ToArray(),
                    requestAttempts);

                await sender.Client.DisconnectAsync();
                restarted = CreateClient(
                    "first-run",
                    "Sender",
                    relay.BaseUrl,
                    model.BaseUrl,
                    root,
                    "sender",
                    secrets: sender.Secrets,
                    initializeIdentity: false);
                var retry = committedDraft with { RunId = "newly-proposed-after-restart" };
                var retried = await new TopicExecutionRouter(
                        restarted.State, restarted.Runner, restarted.Client)
                    .SubmitAsync(retry, null, CancellationToken.None);

                Assert.IsTrue(retried.Accepted, retried.Error);
                Assert.IsTrue(retried.Durable);
                Assert.AreEqual(committedDraft.RunId, retried.RunId);
                Assert.AreEqual("already_completed", retried.Code);
                Assert.AreEqual(1, model.CallCount);
                Assert.AreEqual(
                    requestAttempts.Length,
                    AttemptsFor(await RuntimeAsync(relay.Http), committedDraft.RunId).Length);
                Assert.IsNull(restarted.State.GetTopicOutbox(retry.RunId));
                Assert.AreEqual(
                    1,
                    receiver.State.ListInboundTopicRuns().Count(item =>
                        item.RunId == committedDraft.RunId));
                Assert.AreEqual(
                    1,
                    restarted.State.Profile.OwnThreads.Single(item => item.Id == thread.Id)
                        .Lines.Count(item => item.Id == committedDraft.TriggerLineId));
            }
            finally
            {
                if (sender is not null) await sender.Client.DisconnectAsync();
                if (restarted is not null) await restarted.Client.DisconnectAsync();
                if (receiver is not null) await receiver.Client.DisconnectAsync();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch (IOException) { }
            }
        }
    [TestMethod]
    public async Task ActualProgram_AuthorityAndCliCleanupUseHostedEndpoints()
    {
        var repository = FindRepositoryRoot();
        await using var relay = await RelayProcess.StartAsync(
            TestRelayAssembly(repository), "Test", enabled: true, AdminKey);
        var root = Path.Combine(
            repository, "_artifacts", "elvi-hooks", "authority-state",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var alice = CreateClient("authority", "Alice", relay.BaseUrl, relay.BaseUrl, root, "alice");
        var bob = CreateClient("authority", "Bob", relay.BaseUrl, relay.BaseUrl, root, "bob");
        using var removable = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var removablePublic = Convert.ToBase64String(removable.ExportSubjectPublicKeyInfo());
        LinkedPrivateKeys[removablePublic] =
            Convert.ToBase64String(removable.ExportPkcs8PrivateKey());
        try
        {
            await RegisterLinkedDevicesAsync(relay.Http, alice.State, bob.State, removablePublic);
            var before = await HandleAsync(relay.Http, "authority");
            var original = await ConnectAsync(relay.BaseUrl, bob.State, before);
            await original.Connection.DisposeAsync();

            var removableId = DeviceProtocol.DeviceId(removablePublic);
            using (var revoke = new HttpRequestMessage(
                       HttpMethod.Delete,
                       $"/handles/authority/devices/{removableId}"))
            {
                revoke.Content = JsonContent.Create(new RevokeDeviceRequest(
                    alice.State.Profile.PublicKey,
                    removableId,
                    Sign(
                        alice.State.Profile.PrivateKey,
                        DeviceRevocationProtocol.Message("authority", removableId))));
                using var response = await relay.Http.SendAsync(revoke);
                response.EnsureSuccessStatusCode();
            }

            var after = await HandleAsync(relay.Http, "authority");
            Assert.AreEqual(before.AuthGeneration + 1, after.AuthGeneration);

            var stale = await TryConnectAsync(
                relay.BaseUrl,
                bob.State,
                before,
                (_, canonical) => Sign(bob.State.Profile.PrivateKey, canonical));
            Assert.IsFalse(stale.Authenticated);
            Assert.IsNull(stale.Nonce);
            var replay = await TryConnectAsync(
                relay.BaseUrl,
                bob.State,
                after,
                (_, _) => original.Signature);
            Assert.IsFalse(replay.Authenticated);
            Assert.IsNotNull(replay.Nonce);
            Assert.AreNotEqual(original.Nonce, replay.Nonce);
            var fresh = await TryConnectAsync(
                relay.BaseUrl,
                bob.State,
                after,
                (_, canonical) => Sign(bob.State.Profile.PrivateKey, canonical));
            Assert.IsTrue(fresh.Authenticated);
            Assert.AreNotEqual(original.Nonce, fresh.Nonce);

            var gateDirectory = Path.Combine(root, "cli");
            Directory.CreateDirectory(gateDirectory);
            var gate = Path.Combine(gateDirectory, "fail-gate.ps1");
            await File.WriteAllTextAsync(
                gate,
                "param([string]$RuleId,[string]$RelayBaseUri)\nthrow \"intentional inner failure: $RuleId\"\n");
            const string cliRule = "actual-cli-finally";
            var cli = Path.Combine(repository, "_deploy", "test-relay", "Invoke-MeshLiveFault.ps1");
            var cliResult = await RunProcessAsync(
                "pwsh",
                [
                    "-NoProfile", "-NonInteractive", "-File", cli,
                    "-Action", "Run",
                    "-BaseUri", relay.BaseUrl,
                    "-AdminKey", AdminKey,
                    "-RuleId", cliRule,
                    "-SourceAccount", "authority",
                    "-TargetDevice", bob.Client.MyDeviceId,
                    "-GateScript", gate
                ],
                repository,
                TimeSpan.FromSeconds(30));
            Console.WriteLine(
                $"CLI_PROCESS_RAW_BEGIN exit={cliResult.ExitCode}{Environment.NewLine}" +
                cliResult.Output +
                $"{Environment.NewLine}CLI_PROCESS_RAW_END");
            Assert.AreNotEqual(0, cliResult.ExitCode);
            var status = await GetRuleAsync(relay.Http, cliRule);
            Assert.IsFalse(status.Active);
            Assert.IsNotNull(status.DeactivatedAt);
            var audit = await AuditAsync(relay.Http);
            CollectionAssert.AreEqual(
                new[] { "activated", "deactivated" },
                audit.Where(item => item.RuleId == cliRule)
                    .Select(item => item.Event).ToArray());

            var runtime = await RuntimeAsync(relay.Http);
            Assert.IsTrue(runtime.Handshakes.Any(item =>
                item.Stage == "rejected-before-challenge"
                && item.AuthGeneration == before.AuthGeneration));
            Assert.IsTrue(runtime.Handshakes.Any(item =>
                item.Stage == "authenticate"
                && item.AuthGeneration == after.AuthGeneration
                && item.Accepted == true));
            Console.WriteLine(
                $"ACTUAL_AUTH_CLI oldNonce={original.Nonce} " +
                $"replayNonce={replay.Nonce} freshNonce={fresh.Nonce} staleRejected=true " +
                $"freshAccepted=true cliExit={cliResult.ExitCode} cliActive={status.Active} " +
                $"cliAudit=activated,deactivated");
        }
        finally
        {
            await alice.Client.DisconnectAsync();
            await bob.Client.DisconnectAsync();
            if (alice.State.ActiveAccountId is { } aliceId) alice.State.DeleteAccount(aliceId);
            if (bob.State.ActiveAccountId is { } bobId) bob.State.DeleteAccount(bobId);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    [DataTestMethod]
    [DataRow(DevicePlatforms.Android, true, false, "Android")]
    [DataRow(DevicePlatforms.IOS, true, false, "iOS")]
    [DataRow(DevicePlatforms.IOS, true, false, "iPadOS")]
    [DataRow(DevicePlatforms.Windows, false, false, "ineligible desktop")]
    [DataRow(DevicePlatforms.Windows, true, true, "eligible desktop")]
    public async Task CraftedTopicRequest_ExecutesOnlyOnEligibleRemoteHost(
        string recipientPlatform,
        bool configureRecipientModel,
        bool shouldExecute,
        string scenario)
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(
            repository,
            "_artifacts",
            "remote-host-authorization",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await using var model = await DeterministicModelServer.StartAsync();
        await using var relay = await RelayProcess.StartAsync(
            TestRelayAssembly(repository), "Test", enabled: true, AdminKey);
        ClientHarness? sender = null;
        ClientHarness? recipient = null;
        ConnectionEvidence? senderConnection = null;
        try
        {
            var suffix = Guid.NewGuid().ToString("n")[..8];
            var handle = "remote-auth-" + suffix;
            sender = CreateClient(
                handle, "Sender", relay.BaseUrl, model.BaseUrl, root, "sender");
            recipient = CreateClient(
                handle,
                "Recipient",
                relay.BaseUrl,
                model.BaseUrl,
                root,
                "recipient",
                currentPlatformProvider: () => recipientPlatform,
                configureModel: configureRecipientModel);
            await RegisterLinkedDevicesAsync(
                relay.Http,
                sender.State,
                recipient.State,
                secondaryPlatform: recipientPlatform,
                secondaryRemoteAgentEnabled:
                    DevicePlatforms.IsDesktop(recipientPlatform) && configureRecipientModel);

            await recipient.Client.ConnectAsync();
            await EventuallyAsync(
                () => recipient.Client.Connected,
                TimeSpan.FromSeconds(15));
            var authority = await HandleAsync(relay.Http, handle);
            senderConnection = await ConnectAsync(relay.BaseUrl, sender.State, authority);
            var eligibleDevices = await sender.Client.ListEligibleDevicesAsync(
                CancellationToken.None);
            Assert.AreEqual(
                shouldExecute,
                eligibleDevices.Any(device =>
                    device.DeviceId == recipient.Client.MyDeviceId),
                scenario);
            Assert.IsTrue(
                sender.Client.IsAccountDeviceOnline(recipient.Client.MyDeviceId),
                $"The full account roster must retain online presence for {scenario}.");

            if (!shouldExecute)
            {
                var directAt = DateTimeOffset.UtcNow;
                var directThread = sender.State.NewOwnThread(
                    "Direct authorization",
                    new ExecutionDevice(
                        recipient.Client.MyDeviceId,
                        "Recipient",
                        recipientPlatform),
                    createdAt: directAt);
                var direct = await sender.Client.DispatchAsync(
                    recipient.Client.MyDeviceId,
                    new TopicRunRequestPayload(
                        "run-direct-" + suffix,
                        directThread.Id,
                        "line-direct-" + suffix,
                        handle,
                        "crafted direct request",
                        directAt,
                        recipient.Client.MyDeviceId,
                        TopicTurnMode.Single),
                    [],
                    CancellationToken.None);
                Assert.IsFalse(direct.Accepted, scenario);
                Assert.AreEqual("device_not_eligible", direct.Code, scenario);
                Assert.IsNull(sender.State.GetTopicOutbox(direct.RunId));
            }

            var logs = new ConcurrentQueue<string>();
            recipient.Client.Log += logs.Enqueue;
            var at = DateTimeOffset.UtcNow;
            var request = new TopicRunRequestPayload(
                "run-crafted-" + suffix,
                "thread-crafted-" + suffix,
                "line-crafted-" + suffix,
                handle,
                "crafted inbound request",
                at,
                recipient.Client.MyDeviceId,
                TopicTurnMode.Single);
            var plaintext = TopicRunProtocol.RequestBody(request);
            var ciphertext = MessageCrypto.Encrypt(
                plaintext,
                [recipient.State.Profile.PublicKey]);
            Assert.IsNotNull(ciphertext);
            var envelope = MeshEnvelope.Create(
                handle,
                handle,
                MeshKinds.TopicRunRequest,
                ciphertext,
                Sign(sender.State.Profile.PrivateKey, ciphertext),
                fromDevice: sender.Client.MyDeviceId,
                toDevice: recipient.Client.MyDeviceId,
                id: request.RunId);

            var relayResult = await senderConnection.Connection.InvokeAsync<MeshSendResult>(
                MeshHubProtocol.SendEnvelope,
                envelope,
                CancellationToken.None);
            Assert.IsTrue(relayResult.Accepted, relayResult.Code);

            if (shouldExecute)
            {
                await EventuallyAsync(
                    () => recipient.State.ListInboundTopicRuns().Any(item =>
                        item.RunId == request.RunId
                        && item.State == InboundTopicRunStates.Completed),
                    TimeSpan.FromSeconds(25));
                Assert.AreEqual(1, model.CallCount, scenario);
            }
            else
            {
                await EventuallyAsync(
                    () => logs.Any(message => message.Contains(
                        "receive-permanent-reject: topic_remote_host_not_eligible",
                        StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(15));
                Assert.IsFalse(recipient.State.ListInboundTopicRuns().Any(item =>
                    item.RunId == request.RunId), scenario);
                Assert.AreEqual(0, model.CallCount, scenario);
            }
        }
        finally
        {
            if (senderConnection is not null)
                await senderConnection.Connection.DisposeAsync();
            if (recipient is not null)
                await recipient.Client.DisconnectAsync();
            sender?.State.SignOut();
            recipient?.State.SignOut();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task MobileLocalSelfExecution_RemainsAllowedByRemoteHostGuard()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(
            repository,
            "_artifacts",
            "mobile-local-authorization",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await using var model = await DeterministicModelServer.StartAsync();
        ClientHarness? mobile = null;
        try
        {
            mobile = CreateClient(
                "mobile-local-auth",
                "Mobile",
                "http://127.0.0.1:1",
                model.BaseUrl,
                root,
                "mobile",
                currentPlatformProvider: () => DevicePlatforms.Android);
            var at = DateTimeOffset.UtcNow;
            var thread = mobile.State.NewOwnThread(
                "Local mobile",
                new ExecutionDevice(
                    mobile.Client.MyDeviceId,
                    "Mobile",
                    DevicePlatforms.Android),
                createdAt: at);
            var router = new TopicExecutionRouter(
                mobile.State,
                mobile.Runner,
                mobile.Client,
                () => DevicePlatforms.Android);

            var result = await router.SubmitAsync(
                new TopicTurnDraft(
                    "run-mobile-local",
                    thread.Id,
                    "line-mobile-local",
                    mobile.State.Profile.Handle,
                    "run on this phone",
                    at,
                    TopicTurnMode.Single,
                    mobile.Client.MyDeviceId),
                null,
                CancellationToken.None);

            Assert.IsTrue(result.Accepted, result.Error);
            await EventuallyAsync(
                () => mobile.State.Profile.OwnThreads.Single(item => item.Id == thread.Id)
                          .Lines.Any(line => line.Role == "assistant")
                      && mobile.State.Profile.OwnThreads.Single(item => item.Id == thread.Id)
                          .ExecutionRunId is null,
                TimeSpan.FromSeconds(20));
            Assert.AreEqual(1, model.CallCount);
        }
        finally
        {
            mobile?.State.SignOut();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    private static HttpRequestMessage AdminRequest(
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Mesh-Admin-Key", AdminKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<LiveFaultRuleStatus> ActivateAsync(
        HttpClient http,
        LiveFaultActivationRequest activation)
    {
        using var request = AdminRequest(HttpMethod.Post, "/admin/live-faults", activation);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LiveFaultRuleStatus>())!;
    }

    private static async Task ClearAsync(HttpClient http, string ruleId)
    {
        using var request = AdminRequest(
            HttpMethod.Delete,
            $"/admin/live-faults/{Uri.EscapeDataString(ruleId)}");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<LiveFaultRuleStatus> GetRuleAsync(HttpClient http, string ruleId)
    {
        using var request = AdminRequest(
            HttpMethod.Get,
            $"/admin/live-faults/{Uri.EscapeDataString(ruleId)}");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LiveFaultRuleStatus>())!;
    }

    private static async Task<List<LiveFaultAuditEntry>> AuditAsync(HttpClient http)
    {
        using var request = AdminRequest(HttpMethod.Get, "/admin/live-faults/audit");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<LiveFaultAuditEntry>>())!;
    }

    private static async Task<RuntimeSnapshot> RuntimeAsync(HttpClient http)
    {
        using var request = AdminRequest(HttpMethod.Get, "/admin/live-faults/runtime");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RuntimeSnapshot>())!;
    }

    private static int[] AttemptsFor(RuntimeSnapshot snapshot, string envelopeId)
        => snapshot.Attempts
            .Where(item => item.StableIdHash == LiveFaultIds.Hash(envelopeId))
            .OrderBy(item => item.Sequence)
            .Select(item => item.Attempt)
            .ToArray();

    private static async Task RegisterLinkedDevicesAsync(
        HttpClient http,
        AppState primary,
        AppState secondary,
        string? thirdPublicKey = null,
        string secondaryPlatform = DevicePlatforms.Windows,
        bool secondaryRemoteAgentEnabled = true)
    {
        var handle = AppState.Norm(primary.Profile.Handle);
        var genesis = OnlineReplicationProtocol.CreateCustodyEntry(
            handle,
            0,
            OnlineReplicationProtocol.ZeroHash,
            CustodyAction.Genesis,
            primary.Profile.PublicKey,
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            primary.Profile.PublicKey,
            primary.Profile.PrivateKey);
        using (var response = await http.PostAsJsonAsync(
                   "/handles",
                   new RegisterHandleRequest(
                       handle,
                       primary.Profile.PublicKey,
                       primary.Profile.DisplayName,
                       Signature: Sign(
                           primary.Profile.PrivateKey,
                           ClaimProtocol.Message(handle, primary.Profile.PublicKey)),
                       DeviceName: primary.Profile.DeviceName,
                       DevicePlatform: DevicePlatforms.Windows,
                       RemoteAgentEnabled: true,
                       AgentHostEnabled: true,
                       CustodyAuthority: genesis)))
            response.EnsureSuccessStatusCode();

        await LinkDeviceAsync(http, primary, secondary.Profile.PublicKey);
        if (thirdPublicKey is not null)
            await LinkDeviceAsync(http, primary, thirdPublicKey);

        var authority = await HandleAsync(http, handle);
        Assert.IsNotNull(authority.CustodyAuthority);
        using (var response = await http.PostAsJsonAsync(
                   "/handles",
                   new RegisterHandleRequest(
                       handle,
                       secondary.Profile.PublicKey,
                       secondary.Profile.DisplayName,
                       Signature: Sign(
                           secondary.Profile.PrivateKey,
                           ClaimProtocol.Message(handle, secondary.Profile.PublicKey)),
                       DeviceName: secondary.Profile.DeviceName,
                       DevicePlatform: secondaryPlatform,
                       RemoteAgentEnabled: secondaryRemoteAgentEnabled,
                       AgentHostEnabled: true,
                       CustodyAuthority: authority.CustodyAuthority)))
            response.EnsureSuccessStatusCode();
        primary.ImportCustodyAuthority(handle, authority.CustodyAuthority);
        secondary.ImportCustodyAuthority(handle, authority.CustodyAuthority);
    }

    private static async Task LinkDeviceAsync(
        HttpClient http,
        AppState primary,
        string newPublicKey)
    {
        var handle = AppState.Norm(primary.Profile.Handle);
        var code = Guid.NewGuid().ToString("n");
        var codeHash = LinkProtocol.HashCode(code);
        var expires = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        using (var invite = await http.PostAsJsonAsync(
                   $"/handles/{handle}/link/invite",
                   new LinkInviteRequest(
                       handle,
                       primary.Profile.PublicKey,
                       codeHash,
                       expires,
                       Sign(
                           primary.Profile.PrivateKey,
                           LinkProtocol.InviteMessage(handle, codeHash, expires)))))
            invite.EnsureSuccessStatusCode();

        var privateKey = string.Equals(
            newPublicKey, primary.Profile.PublicKey, StringComparison.Ordinal)
            ? primary.Profile.PrivateKey
            : FindPrivateKey(newPublicKey);
        using var redeem = await http.PostAsJsonAsync(
            $"/handles/{handle}/link/redeem",
            new LinkRedeemRequest(
                handle,
                newPublicKey,
                code,
                Sign(privateKey, LinkProtocol.RedeemMessage(handle, code))));
        redeem.EnsureSuccessStatusCode();
    }

    private static readonly ConcurrentDictionary<string, string> LinkedPrivateKeys =
        new(StringComparer.Ordinal);

    private static string FindPrivateKey(string publicKey)
        => LinkedPrivateKeys.TryGetValue(publicKey, out var privateKey)
            ? privateKey
            : throw new InvalidOperationException("Linked private key was not registered.");

    private static async Task<HandleInfo> HandleAsync(HttpClient http, string handle)
        => (await http.GetFromJsonAsync<HandleInfo>(
            $"/handles/{Uri.EscapeDataString(handle)}"))!;

    private static async Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> DevicesAsync(
        HttpClient http,
        string handle)
        => (await http.GetFromJsonAsync<List<Mesh.Shared.DeviceInfo>>(
            $"/handles/{Uri.EscapeDataString(handle)}/devices"))!;

    private ClientHarness CreateClient(
        string handle,
        string name,
        string relayUrl,
        string modelUrl,
        string root,
        string suffix,
        TimeProvider? timeProvider = null,
        MemorySecretStore? secrets = null,
        bool initializeIdentity = true,
        Func<string>? currentPlatformProvider = null,
        bool configureModel = true)
    {
        timeProvider ??= TimeProvider.System;
        secrets ??= new MemorySecretStore();
        var state = new AppState(
            secrets,
            timeProvider,
            StoragePaths.ForRoot(Path.Combine(root, suffix)));
        if (initializeIdentity)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            state.Profile.PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            state.Profile.PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            LinkedPrivateKeys[state.Profile.PublicKey] = state.Profile.PrivateKey;
            state.Profile.Handle = handle;
            state.Profile.DisplayName = name;
            state.Profile.DeviceName = name;
            state.Profile.RelayUrl = relayUrl;
            if (configureModel)
            {
                state.Profile.Model.Provider = ModelProvider.FoundryLocal;
                state.Profile.Model.Model = "deterministic-boundary";
                state.Profile.Model.Endpoint = modelUrl;
                state.Profile.Model.ApiKey = "test-boundary";
            }
            state.Save();
        }
        var http = new RealHttpClientFactory();
        var meter = new TokenMeter(state);
        var media = new AgentMedia();
        var memory = new MemoryService(state);
        var factory = new ModelFactory(http, state, meter, new BrowserModelService(), null!);
        var tools = new ToolRegistry(
            null!, null!, null!, http, null!, new LocalFileRegistry(),
            null!, media, null!, state, null!);
        var agent = new AgentService(
            state,
            factory,
            new FoundryLocalService(http),
            tools,
            meter,
            media,
            memory,
            new EmptyBuiltIns());
        var runner = new TopicTurnRunner(agent, state);
        var client = new MeshClient(
            state,
            agent,
            runner,
            http,
            new NoopPushService(),
            new ForegroundLifecycle(),
            timeProvider,
            currentPlatformProvider: currentPlatformProvider);
        var harness = new ClientHarness(state, client, runner, secrets);
        createdClients.Add(harness);
        return harness;
    }

    private static void CreatePreTriggerActiveCorrelationFixture(
        string databasePath,
        byte[] databaseKey,
        string runId,
        string threadId,
        string targetDeviceId,
        DateTimeOffset createdAt)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={databasePath}");
        connection.Open();
        using (var key = connection.CreateCommand())
        {
            key.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(databaseKey)}'\";";
            key.ExecuteNonQuery();
        }
        using var fixture = connection.CreateCommand();
        fixture.CommandText = """
            INSERT OR REPLACE INTO topic_run_correlations(
                run_id, thread_id, target_device_id, trigger_line_id, created_at,
                terminal_at, terminal_event_at, trigger_identity_state)
            VALUES($run, $thread, $target, NULL, $created, NULL, NULL, 'strict');
            INSERT INTO meta(k, v) VALUES('topic_run_trigger_schema_version', '1')
            ON CONFLICT(k) DO UPDATE SET v = excluded.v;
            ALTER TABLE topic_run_correlations DROP COLUMN trigger_identity_state;
            ALTER TABLE topic_run_correlations DROP COLUMN trigger_line_id;
            """;
        fixture.Parameters.AddWithValue("$run", runId);
        fixture.Parameters.AddWithValue("$thread", threadId);
        fixture.Parameters.AddWithValue("$target", targetDeviceId);
        fixture.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        fixture.ExecuteNonQuery();
    }


    private static async Task<ConnectionEvidence> ConnectAsync(
        string baseUrl,
        AppState state,
        HandleInfo authority)
    {
        var result = await TryConnectAsync(
            baseUrl,
            state,
            authority,
            (_, canonical) => Sign(state.Profile.PrivateKey, canonical));
        Assert.IsTrue(result.Authenticated);
        Assert.IsNotNull(result.Signature);
        return new ConnectionEvidence(result.Connection!, result.Nonce!, result.Signature!);
    }

    private static async Task<ConnectionAttempt> TryConnectAsync(
        string baseUrl,
        AppState state,
        HandleInfo authority,
        Func<string, string, string> signatureFactory)
    {
        var handle = AppState.Norm(state.Profile.Handle);
        var deviceId = DeviceProtocol.DeviceId(state.Profile.PublicKey);
        var url =
            $"{baseUrl}{MeshHubProtocol.Route}?handle={Uri.EscapeDataString(handle)}" +
            $"&deviceId={Uri.EscapeDataString(deviceId)}" +
            $"&protocolVersion={MeshProtocol.Version}" +
            $"&authGeneration={authority.AuthGeneration}" +
            $"&custodyHead={Uri.EscapeDataString(authority.CustodyHead ?? "")}";
        var connection = new HubConnectionBuilder().WithUrl(url).Build();
        var presence = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? nonce = null;
        string? signature = null;
        connection.On<string>(MeshHubProtocol.Challenge, value =>
        {
            nonce = value;
            var canonical = RelayConnectChallenge.Canonical(
                value,
                handle,
                deviceId,
                MeshProtocol.Version,
                authority.AuthGeneration,
                authority.CustodyHead ?? "");
            signature = signatureFactory(value, canonical);
            return connection.SendAsync(
                MeshHubProtocol.Authenticate,
                state.Profile.PublicKey,
                signature);
        });
        connection.On<PresenceConfirmed>(
            MeshHubProtocol.PresenceConfirmed,
            _ => presence.TrySetResult(true));
        connection.Closed += _ =>
        {
            closed.TrySetResult(true);
            return Task.CompletedTask;
        };
        try
        {
            await connection.StartAsync();
            await Task.WhenAny(presence.Task, closed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (presence.Task.IsCompletedSuccessfully)
                return new ConnectionAttempt(true, nonce, signature, connection);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HOSTED_CONNECT_FAILURE {ex}");
        }
        await connection.DisposeAsync();
        return new ConnectionAttempt(false, nonce, signature, null);
    }

    private static string Sign(string privateKeyB64, string message)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        return Convert.ToBase64String(
            key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
    }

    private static async Task EventuallyAsync(
        Func<bool> condition,
        TimeSpan timeout,
        Func<string>? diagnostics = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                Assert.Fail(
                    "Timed out waiting for the actual Program runtime condition." +
                    (diagnostics is null ? "" : " " + diagnostics()));
            await Task.Delay(50);
        }
    }

    private static async Task EventuallyAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                Assert.Fail("Timed out waiting for the actual Program runtime condition.");
            await Task.Delay(50);
        }
    }

    private static async Task EventuallyStableAsync(
        Func<bool> condition,
        TimeSpan stableFor,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        DateTimeOffset? stableSince = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                stableSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - stableSince >= stableFor)
                    return;
            }
            else
            {
                stableSince = null;
            }
            await Task.Delay(50);
        }
        Assert.Fail("Timed out waiting for the actual Program runtime condition to remain stable.");
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
        throw new DirectoryNotFoundException("Mesh repository root was not found.");
    }

    private static string TestRelayAssembly(string repository)
        => Path.Combine(
            repository, "src", "Mesh.Relay", "bin", "Debug", "net10.0",
            "test-hooks", "Mesh.Relay.TestHooks.dll");

    private static async Task<string> EnsureProductionRelayAsync(string repository)
    {
        var assembly = Path.Combine(
            repository, "src", "Mesh.Relay", "bin", "Debug", "net10.0",
            "production", "Mesh.Relay.dll");
        if (File.Exists(assembly)) return assembly;
        var result = await RunProcessAsync(
            "dotnet",
            [
                "build",
                Path.Combine(repository, "src", "Mesh.Relay", "Mesh.Relay.csproj"),
                "-c", "Debug",
                "-p:MeshRelayTestFaults=false",
                "--no-restore",
                "--nologo"
            ],
            repository,
            TimeSpan.FromMinutes(2));
        Assert.AreEqual(0, result.ExitCode, result.Output);
        Assert.IsTrue(File.Exists(assembly), $"Production relay was not built at {assembly}.");
        return assembly;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException($"Could not launch {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(timeout);
        return new ProcessResult(
            process.ExitCode,
            (await stdout) + Environment.NewLine + (await stderr));
    }

    private sealed record ClientHarness(
        AppState State,
        MeshClient Client,
        ITopicTurnRunner Runner,
        MemorySecretStore Secrets);

    private sealed class GeneratedTopicFaultScheduler(
        string runId,
        string wrongTriggerLineId) : ITopicEnvelopeTestFaultScheduler
    {
        private readonly ConcurrentDictionary<string, DelayedSend> delayed =
            new(StringComparer.Ordinal);
        private int released;
        private int mutatedTerminalCount;
        private int correctTerminalCount;
        private int delayedAcceptedCount;
        private int delayedRunningCount;

        public int MutatedTerminalCount => Volatile.Read(ref mutatedTerminalCount);
        public int CorrectTerminalCount => Volatile.Read(ref correctTerminalCount);
        public int DelayedAcceptedCount => Volatile.Read(ref delayedAcceptedCount);
        public int DelayedRunningCount => Volatile.Read(ref delayedRunningCount);

        public async Task<MeshSendResult?> SendAsync(
            TopicEnvelopeSendAttempt attempt,
            Func<TopicEnvelopeSendAttempt, CancellationToken, Task<MeshSendResult?>> send,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(attempt.Kind, MeshKinds.TopicRunUpdate, StringComparison.Ordinal)
                || !TopicRunProtocol.TryParseUpdate(attempt.Plaintext, out var update)
                || !string.Equals(update.RunId, runId, StringComparison.Ordinal))
                return await send(attempt, cancellationToken);

            if (TopicControlProtocol.IsTerminal(update))
            {
                if (Volatile.Read(ref released) == 1)
                {
                    Interlocked.Increment(ref correctTerminalCount);
                    return await send(attempt, cancellationToken);
                }
                delayed["terminal"] = new DelayedSend(attempt, send);
                if (Interlocked.CompareExchange(ref mutatedTerminalCount, 1, 0) == 0)
                {
                    var mutated = update with { TriggerLineId = wrongTriggerLineId };
                    return await send(
                        attempt with { Plaintext = TopicRunProtocol.UpdateBody(mutated) },
                        cancellationToken);
                }
                return MeshSendResult.Ok();
            }

            if (Volatile.Read(ref released) == 0
                && TopicControlProtocol.IsAcceptance(update))
            {
                if (delayed.TryAdd("accepted", new DelayedSend(attempt, send)))
                    Interlocked.Increment(ref delayedAcceptedCount);
                return MeshSendResult.Ok();
            }
            if (Volatile.Read(ref released) == 0
                && update.Phase == TopicRunPhase.Executing)
            {
                if (delayed.TryAdd("running", new DelayedSend(attempt, send)))
                    Interlocked.Increment(ref delayedRunningCount);
                return MeshSendResult.Ok();
            }
            return await send(attempt, cancellationToken);
        }

        public async Task ReleaseAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 1) return;
            foreach (var key in new[] { "running", "accepted", "terminal" })
            {
                if (!delayed.TryGetValue(key, out var item)) continue;
                if (key == "terminal") Interlocked.Increment(ref correctTerminalCount);
                var result = await item.Send(item.Attempt, CancellationToken.None);
                if (result is null || !result.Accepted)
                    throw new InvalidOperationException(
                        $"Generated delayed {key} envelope was not relay-accepted.");
            }
        }

        private sealed record DelayedSend(
            TopicEnvelopeSendAttempt Attempt,
            Func<TopicEnvelopeSendAttempt, CancellationToken, Task<MeshSendResult?>> Send);
    }

    private sealed class GeneratedReplicationPairScheduler(
        string threadId,
        bool upsertFirst) : IReplicationEventTestFaultScheduler
    {
        private readonly object gate = new();
        private readonly List<(GeneratedReplicationEvent Event, Action Persist)> held = [];
        private readonly TaskCompletionSource captured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool assistantSeen;

        public Task Captured => captured.Task;
        public IReadOnlyList<ReplicationPayloadCodec.DomainAction> ReleasedOrder { get; private set; } = [];

        public bool Schedule(GeneratedReplicationEvent generated, Action persist)
        {
            if (!string.Equals(generated.Kind, ReplicationOpKinds.Topic, StringComparison.Ordinal)
                || !string.Equals(generated.EntityId, threadId, StringComparison.Ordinal))
                return false;
            lock (gate)
            {
                if (held.Count >= 2) return false;
                if (generated.Action == ReplicationPayloadCodec.DomainAction.AppendLine)
                {
                    var line = JsonSerializer.Deserialize<ChatLine>(
                        generated.BodyJson,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (!string.Equals(line?.Role, "assistant", StringComparison.Ordinal))
                        return false;
                    assistantSeen = true;
                    held.Add((generated, persist));
                    return true;
                }
                if (!assistantSeen
                    || generated.Action != ReplicationPayloadCodec.DomainAction.Upsert)
                    return false;
                held.Add((generated, persist));
                captured.TrySetResult();
                return true;
            }
        }

        public void Release()
        {
            (GeneratedReplicationEvent Event, Action Persist)[] release;
            lock (gate)
            {
                Assert.AreEqual(2, held.Count);
                release = upsertFirst
                    ? [held[1], held[0]]
                    : [held[0], held[1]];
                held.Clear();
            }
            ReleasedOrder = release.Select(item => item.Event.Action).ToArray();
            foreach (var item in release) item.Persist();
        }
    }

    private sealed class GeneratedAssistantAppendDelayScheduler(string threadId)
        : IReplicationEventTestFaultScheduler
    {
        private readonly TaskCompletionSource captured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? persist;

        public Task Captured => captured.Task;

        public bool Schedule(GeneratedReplicationEvent generated, Action emit)
        {
            if (persist is not null
                || !string.Equals(generated.Kind, ReplicationOpKinds.Topic, StringComparison.Ordinal)
                || !string.Equals(generated.EntityId, threadId, StringComparison.Ordinal)
                || generated.Action != ReplicationPayloadCodec.DomainAction.AppendLine)
                return false;
            var line = JsonSerializer.Deserialize<ChatLine>(
                generated.BodyJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!string.Equals(line?.Role, "assistant", StringComparison.Ordinal))
                return false;
            persist = emit;
            captured.TrySetResult();
            return true;
        }

        public void Release()
        {
            var pending = Interlocked.Exchange(ref persist, null)
                          ?? throw new InvalidOperationException("No generated assistant event was delayed.");
            pending();
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
    private sealed record ConnectionEvidence(
        HubConnection Connection,
        string Nonce,
        string Signature);
    private sealed record ConnectionAttempt(
        bool Authenticated,
        string? Nonce,
        string? Signature,
        HubConnection? Connection);
    private sealed record RuntimeSnapshot(
        List<LiveFaultTransportAttempt> Attempts,
        List<LiveFaultHandshakeEvent> Handshakes);

    private sealed class RelayProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly List<string> output;

        private RelayProcess(Process process, string baseUrl, List<string> output)
        {
            this.process = process;
            this.output = output;
            BaseUrl = baseUrl;
            Http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        }

        public string BaseUrl { get; }
        public HttpClient Http { get; }
        public int ProcessId => process.Id;
        public int Port => new Uri(BaseUrl).Port;
        public bool HasExited => process.HasExited;
        public string OutputTail
        {
            get
            {
                lock (output)
                    return string.Join(" | ", output.TakeLast(8));
            }
        }

        public static async Task<RelayProcess> StartAsync(
            string assembly,
            string environment,
            bool enabled,
            string adminKey)
        {
            Assert.IsTrue(File.Exists(assembly), $"Relay assembly does not exist: {assembly}");
            var port = FreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var output = new List<string>();
            var start = StartInfo(assembly, baseUrl, environment, enabled, adminKey);
            var process = Process.Start(start)
                          ?? throw new InvalidOperationException("Could not launch relay Program.");
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) lock (output) output.Add(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) lock (output) output.Add(e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var relay = new RelayProcess(process, baseUrl, output);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited)
                    Assert.Fail(
                        $"Relay Program exited {process.ExitCode}:{Environment.NewLine}" +
                        string.Join(Environment.NewLine, output));
                try
                {
                    using var response = await relay.Http.GetAsync("/health");
                    if (response.IsSuccessStatusCode) return relay;
                }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            await relay.DisposeAsync();
            Assert.Fail("Relay Program did not become healthy.");
            throw new InvalidOperationException();
        }

        public static async Task<ProcessResult> StartExpectingFailureAsync(
            string assembly,
            string environment,
            bool enabled,
            string adminKey)
        {
            var port = FreePort();
            return await RunConfiguredAsync(
                StartInfo(
                    assembly,
                    $"http://127.0.0.1:{port}",
                    environment,
                    enabled,
                    adminKey),
                TimeSpan.FromSeconds(20));
        }

        private static ProcessStartInfo StartInfo(
            string assembly,
            string baseUrl,
            string environment,
            bool enabled,
            string adminKey)
        {
            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(assembly)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(assembly);
            start.Environment["ASPNETCORE_URLS"] = baseUrl;
            start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
            start.Environment["MESH_LIVE_FAULTS_ENABLED"] = enabled ? "true" : "false";
            start.Environment["MESH_ADMIN_KEY"] = adminKey;
            start.Environment["MESH_REQUIRE_METADATA_STORAGE"] = "false";
            start.Environment["MESH_MSG_RATE_PER_MIN"] = "6000";
            start.Environment["MESH_MSG_BURST"] = "1000";
            return start;
        }

        private static async Task<ProcessResult> RunConfiguredAsync(
            ProcessStartInfo start,
            TimeSpan timeout)
        {
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Could not launch relay Program.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                var output = (await stdout) + Environment.NewLine + (await stderr);
                throw new TimeoutException(
                    $"Relay Program did not exit. file={start.FileName}; " +
                    $"arguments={string.Join(' ', start.ArgumentList)}; output={output}");
            }
            return new ProcessResult(
                process.ExitCode,
                (await stdout) + Environment.NewLine + (await stderr));
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
            lock (output)
                Console.WriteLine(
                    "RELAY_PROGRAM_OUTPUT" + Environment.NewLine +
                    string.Join(Environment.NewLine, output));
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class DeterministicModelServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private int calls;
        private TaskCompletionSource? nextResponseGate;
        private readonly TaskCompletionSource firstResponseCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private DeterministicModelServer(WebApplication app, string baseUrl)
        {
            this.app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }
        public int ProcessId => Environment.ProcessId;
        public int Port => new Uri(BaseUrl).Port;
        public int CallCount => Volatile.Read(ref calls);
        public Task FirstResponseCompleted => firstResponseCompleted.Task;
        public void PauseNextResponse()
            => nextResponseGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseResponse()
            => Interlocked.Exchange(ref nextResponseGate, null)?.TrySetResult();

        public static async Task<DeterministicModelServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            DeterministicModelServer? server = null;
            app.MapPost("/v1/chat/completions", async (HttpContext context) =>
            {
                Interlocked.Increment(ref server!.calls);
                var gate = server.nextResponseGate;
                if (gate is not null) await gate.Task;
                await context.Response.WriteAsJsonAsync(new
                {
                    id = "deterministic-test",
                    model = "deterministic-boundary",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = "deterministic hosted response"
                            },
                            finish_reason = "stop"
                        }
                    },
                    usage = new
                    {
                        prompt_tokens = 1,
                        completion_tokens = 1,
                        total_tokens = 2
                    }
                });
                server.firstResponseCompleted.TrySetResult();
            });
            await app.StartAsync();
            var baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server = new DeterministicModelServer(app, baseUrl);
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> keys = [];
        public byte[] GetOrCreateDbKey(string identityId)
            => keys.TryGetValue(identityId, out var key)
                ? key
                : keys[identityId] = RandomNumberGenerator.GetBytes(32);
        public byte[]? GetDbKey(string identityId) => keys.GetValueOrDefault(identityId);
        public void PutDbKey(string identityId, byte[] key) => keys[identityId] = key.ToArray();
        public void DeleteDbKey(string identityId) => keys.Remove(identityId);
    }

    private sealed class EmptyBuiltIns : IBuiltInContentProvider
    {
        public IReadOnlyList<BuiltInPolicy> GetPolicies(AgentRole role) => [];
        public IReadOnlyList<KnowledgeItem> GetKnowledge(AgentRole role) => [];
        public IReadOnlyList<Skill> GetSkills(AgentRole role) => [];
        public KnowledgeItem? LoadKnowledge(string id) => null;
        public Skill? LoadSkill(string id) => null;
    }

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class ForegroundLifecycle : IAppLifecycleState
    {
        public bool IsForeground => true;
        public event Action<bool>? ForegroundChanged
        {
            add { }
            remove { }
        }
    }
}
