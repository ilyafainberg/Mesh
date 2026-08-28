using System.Security.Cryptography;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.ComponentTests;

[TestClass]
public sealed class Protocol9RunReconciliationWiringTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<(AppState State, string Root)> states = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var (state, _) in states)
            state.SignOut();
        SqliteConnection.ClearAllPools();
        foreach (var root in states.Select(item => item.Root).Distinct(StringComparer.Ordinal))
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CommittedAnswerAndNullTopicUpsert_ReconcileFromFinalBatchInEitherOrder(
        bool upsertFirst)
    {
        var at = DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var (state, secrets) = CreateState(at);
        var command = Begin(state, "run-batch", "line-batch", at);
        var answer = new ChatLine
        {
            Id = "answer-batch",
            Role = "assistant",
            Text = "done",
            ReplyToLineId = command.Draft.TriggerLineId,
            At = at.AddMinutes(1)
        };
        var upsert = new OwnThread
        {
            Id = command.Draft.ThreadId,
            Title = "Replicated title",
            CreatedAt = at,
            ExecutionRunId = null,
            LastActivityAt = answer.At
        };
        var appendEnvelope = Envelope(
            ReplicationPayloadCodec.DomainAction.AppendLine,
            command.Draft.ThreadId,
            answer);
        var upsertEnvelope = Envelope(
            ReplicationPayloadCodec.DomainAction.Upsert,
            command.Draft.ThreadId,
            upsert);

        await ApplyProductionBatchAsync(
            state,
            secrets,
            upsertFirst ? [upsertEnvelope, appendEnvelope] : [appendEnvelope, upsertEnvelope]);

        Assert.IsNull(state.Profile.OwnThreads.Single().ExecutionRunId);
        Assert.IsNull(state.GetTopicOutbox(command.Draft.RunId));
        using var verificationDb = OpenStateDb(state, secrets);
        var correlation = verificationDb.GetTopicRunCorrelation(command.Draft.RunId);
        Assert.IsNotNull(correlation?.TerminalAt);
        Assert.AreEqual(command.Draft.TriggerLineId, correlation.TriggerLineId);
    }

    [TestMethod]
    public void ProductionStartup_ReconcilesDurableAnswerWithoutTestCallingPostCommitHook()
    {
        AssertStartupRecoveryUsesProductionTrigger();
        var at = DateTimeOffset.Parse("2026-08-26T00:30:00Z");
        var (state, secrets) = CreateState(at);
        var command = Begin(state, "run-startup", "line-startup", at);
        state.FlushPersistenceAsync().GetAwaiter().GetResult();
        var answer = new ChatLine
        {
            Id = "answer-startup",
            Role = "assistant",
            Text = "durable before restart",
            ReplyToLineId = command.Draft.TriggerLineId,
            At = at.AddMinutes(1)
        };
        SeedProductionProjectionWithoutPostCommit(
            state,
            secrets,
            Envelope(
                ReplicationPayloadCodec.DomainAction.AppendLine,
                command.Draft.ThreadId,
                answer));

        var restarted = new AppState(
            secrets,
            new ManualTimeProvider(at.AddMinutes(2)),
            StoragePaths.ForRoot(state.StorageRoot));
        states.Add((restarted, state.StorageRoot));

        Assert.IsNull(restarted.Profile.OwnThreads.Single().ExecutionRunId);
        Assert.IsNull(restarted.GetTopicOutbox(command.Draft.RunId));
        using var verificationDb = OpenStateDb(restarted, secrets);
        Assert.IsNotNull(verificationDb.GetTopicRunCorrelation(command.Draft.RunId)?.TerminalAt);
    }

    private static void AssertStartupRecoveryUsesProductionTrigger()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? source = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tests",
                "Mesh.App.ComponentTests",
                "Protocol9RunReconciliationWiringTests.cs");
            if (File.Exists(candidate))
            {
                source = File.ReadAllText(candidate);
                break;
            }
            directory = directory.Parent;
        }
        Assert.IsNotNull(source, "the startup recovery source guard could not locate its test source");
        var start = source.IndexOf(
            "public void ProductionStartup_ReconcilesDurableAnswerWithoutTestCallingPostCommitHook",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private static void AssertStartupRecoveryUsesProductionTrigger",
            start,
            StringComparison.Ordinal);
        var testBody = source[start..end];
        Assert.IsFalse(testBody.Contains("ReconcileCommittedTopicAnswersAfterBatch", StringComparison.Ordinal));
        Assert.IsFalse(testBody.Contains("ReconcileTopicRunWithAnswer", StringComparison.Ordinal));
        Assert.IsFalse(testBody.Contains("ApplyReplicatedStateBatchAfterCommitAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OlderCorrelatedAnswerWithAdvancedTimestamp_DoesNotFinalizeNewerActiveRun()
    {
        var at = DateTimeOffset.Parse("2026-08-26T01:00:00Z");
        var (state, secrets) = CreateState(at);
        var old = Begin(state, "run-old", "line-old", at);
        var newer = Begin(state, "run-new", "line-new", at.AddMinutes(1));
        state.SetAgentRun(new AgentRunState(
            newer.Draft.RunId,
            newer.Draft.ThreadId,
            AgentRunPhase.Executing,
            "",
            [],
            at.AddMinutes(1)));

        await ApplyProductionBatchAsync(
            state,
            secrets,
            [
                Envelope(
                    ReplicationPayloadCodec.DomainAction.AppendLine,
                    old.Draft.ThreadId,
                    new ChatLine
                    {
                        Id = "answer-old",
                        Role = "assistant",
                        Text = "old answer",
                        ReplyToLineId = old.Draft.TriggerLineId,
                        At = at.AddHours(12)
                    })
            ]);

        Assert.AreEqual(newer.Draft.RunId, state.Profile.OwnThreads.Single().ExecutionRunId);
        Assert.IsNotNull(state.GetTopicOutbox(old.Draft.RunId));
        Assert.IsNotNull(state.GetTopicOutbox(newer.Draft.RunId));
    }

    [TestMethod]
    public async Task LegacyUncorrelatedAnswer_FallsBackOnlyWithoutDurableRunIdentity()
    {
        var at = DateTimeOffset.Parse("2026-08-26T02:00:00Z");
        var (legacyState, legacySecrets) = CreateState(at);
        legacyState.RegisterExpectedRemoteRun(
            "thread",
            "legacy-run",
            new ExecutionDevice("device-b", "Device B", "Windows"),
            at);
        await ApplyProductionBatchAsync(
            legacyState,
            legacySecrets,
            [Envelope(
                ReplicationPayloadCodec.DomainAction.AppendLine,
                "thread",
                new ChatLine
                {
                    Id = "legacy-answer",
                    Role = "assistant",
                    Text = "legacy",
                    At = at.AddMinutes(1)
                })]);
        Assert.IsNull(legacyState.Profile.OwnThreads.Single().ExecutionRunId);

        var (durableState, durableSecrets) = CreateState(at);
        var durable = Begin(durableState, "durable-run", "durable-line", at);
        await ApplyProductionBatchAsync(
            durableState,
            durableSecrets,
            [Envelope(
                ReplicationPayloadCodec.DomainAction.AppendLine,
                "thread",
                new ChatLine
                {
                    Id = "uncorrelated-answer",
                    Role = "assistant",
                    Text = "must be fenced",
                    At = at.AddHours(1)
                })]);
        Assert.AreEqual(durable.Draft.RunId, durableState.Profile.OwnThreads.Single().ExecutionRunId);
        Assert.IsNotNull(durableState.GetTopicOutbox(durable.Draft.RunId));
    }

    [TestMethod]
    public void TopicControlCorrelation_RequiresExactTriggerForCurrentAndRetainedReplay()
    {
        var at = DateTimeOffset.Parse("2026-08-26T03:00:00Z");
        var (state, _) = CreateState(at);
        var command = Begin(state, "run-trigger", "line-trigger", at);
        var exact = command.InitialProjection with
        {
            Phase = TopicRunPhase.Completed,
            Status = "Completed",
            Timestamp = at.AddMinutes(1)
        };
        var wrong = exact with { TriggerLineId = "different-trigger" };

        Assert.IsFalse(state.IsExpectedTopicRunCorrelation(
            wrong, command.Target.DeviceId, allowRetained: true));
        Assert.IsTrue(state.IsExpectedTopicRunCorrelation(
            exact, command.Target.DeviceId, allowRetained: true));
        Assert.AreEqual(
            RemoteTopicUpdatePersistenceResult.Applied,
            state.ApplyRemoteTopicUpdate(exact, command.Target.DeviceId));
        Assert.IsFalse(state.IsExpectedTopicRunCorrelation(
            wrong, command.Target.DeviceId, allowRetained: true));
        Assert.IsTrue(state.IsExpectedTopicRunCorrelation(
            exact, command.Target.DeviceId, allowRetained: true));
    }

    private (AppState State, MemorySecretStore Secrets) CreateState(DateTimeOffset at)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory, "terminal-reconciliation", Guid.NewGuid().ToString("n"));
        var secrets = new MemorySecretStore();
        var state = new AppState(
            secrets,
            new ManualTimeProvider(at),
            StoragePaths.ForRoot(root));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        state.Profile.Handle = "owner";
        state.Profile.DisplayName = "Owner";
        state.Profile.DeviceName = "Requester";
        state.Profile.PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        state.Profile.PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        state.Profile.Model.ApiKey = "configured";
        state.Profile.OwnThreads.Add(new OwnThread
        {
            Id = "thread",
            Title = "Terminal reconciliation",
            CreatedAt = at,
            LastActivityAt = at
        });
        state.Save();
        states.Add((state, root));
        return (state, secrets);
    }

    private static TopicRunBeginCommand Begin(
        AppState state,
        string runId,
        string triggerLineId,
        DateTimeOffset at)
    {
        var draft = new TopicTurnDraft(
            runId,
            "thread",
            triggerLineId,
            "owner",
            "prompt",
            at,
            TopicTurnMode.Single,
            "executor");
        var request = new TopicRunRequestPayload(
            runId,
            draft.ThreadId,
            triggerLineId,
            draft.TriggerHandle,
            draft.Prompt,
            at,
            "executor",
            TopicTurnMode.Single);
        var command = new TopicRunBeginCommand(
            draft,
            new ExecutionDevice("executor", "Executor", DevicePlatforms.Windows),
            TopicRunBeginMode.Remote,
            TopicAcceptancePolicy.Create(request, at),
            request,
            []);
        var result = state.BeginTopicRun(command);
        Assert.IsTrue(result.Committed, result.Code);
        return command;
    }

    private static ReplicationPayloadCodec.DomainEnvelope Envelope<T>(
        ReplicationPayloadCodec.DomainAction action,
        string threadId,
        T body)
        => new(
            ReplicationOpKinds.Topic,
            action,
            threadId,
            threadId,
            Guid.NewGuid().ToString("n"),
            JsonSerializer.Serialize(body, Json));

    private static async Task ApplyProductionBatchAsync(
        AppState state,
        MemorySecretStore secrets,
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> envelopes)
    {
        using var db = OpenStateDb(state, secrets);
        var applier = state.CreateReplicationApplier();
        var committed = new List<ReplicationCommittedDomainEvent>();
        using (var transaction = db.RawConnectionForTest.BeginTransaction())
        {
            foreach (var envelope in envelopes)
            {
                var evt = new ReplicationEvent(
                    Guid.NewGuid().ToString("n"),
                    envelope.ConversationId ?? envelope.EntityId,
                    "owner",
                    "executor",
                    "epoch",
                    (ulong)(committed.Count + 1),
                    0,
                    envelope.Kind,
                    envelope.EntityId,
                    envelope.CausalVersion,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "cipher",
                    "hash",
                    "signature");
                var prepared = applier.Prepare(evt, envelope, deviceIsDesktop: true);
                Assert.IsNotNull(prepared);
                _ = applier.Apply(
                    db.RawConnectionForTest,
                    transaction,
                    evt,
                    prepared,
                    deviceIsDesktop: true);
                committed.Add(new ReplicationCommittedDomainEvent(evt, prepared));
            }
            transaction.Commit();
        }
        await applier.AfterCommitBatchAsync(committed, deviceIsDesktop: true);
    }

    private static void SeedProductionProjectionWithoutPostCommit(
        AppState state,
        MemorySecretStore secrets,
        ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        using var db = OpenStateDb(state, secrets);
        var applier = state.CreateReplicationApplier();
        using var transaction = db.RawConnectionForTest.BeginTransaction();
        var evt = new ReplicationEvent(
            Guid.NewGuid().ToString("n"),
            envelope.ConversationId ?? envelope.EntityId,
            "owner",
            "executor",
            "epoch",
            1,
            0,
            envelope.Kind,
            envelope.EntityId,
            envelope.CausalVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "cipher",
            "hash",
            "signature");
        var prepared = applier.Prepare(evt, envelope, deviceIsDesktop: true);
        Assert.IsNotNull(prepared);
        _ = applier.Apply(
            db.RawConnectionForTest,
            transaction,
            evt,
            prepared,
            deviceIsDesktop: true);
        transaction.Commit();
    }

    private static MeshDb OpenStateDb(AppState state, MemorySecretStore secrets)
        => MeshDb.Open(
            state.ActiveDatabasePath!,
            secrets.GetDbKey(state.ActiveAccountId!)!);

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

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
