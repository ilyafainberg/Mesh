#if DEVICE_TOPIC_EXECUTION_TESTS
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Services
{
    // Lightweight state seam used when TopicExecutionRouter.cs is linked into the pure net10 test
    // project. The production AppState is intentionally not linked because it brings in MAUI services.
    public sealed class AppState
    {
        public MeshProfile Profile { get; } = new();
        public int RegisteredRemoteRuns { get; private set; }
        public int ClearedRemoteRuns { get; private set; }
        public bool Busy { get; private set; }
        public bool RemoteRunUpdatePersistenceSucceeds { get; set; } = true;
        public QueuedTopicRunState QueuedRuns { get; } = new();

        public static string Norm(string value) => value.Trim().TrimStart('@').ToLowerInvariant();

        public void AddOwnChatLine(string threadId, ChatLine line)
            => Profile.OwnThreads.Single(thread => thread.Id == threadId).Lines.Add(line);

        public void BindOwnThreadForSend(string threadId, ExecutionDevice target)
        {
            var thread = Profile.OwnThreads.Single(item => item.Id == threadId);
            if (thread.ExecutionDeviceId is not null)
                throw new InvalidOperationException();
            thread.ExecutionDeviceId = target.DeviceId;
            thread.ExecutionDeviceName = target.DeviceName;
            thread.ExecutionDevicePlatform = target.Platform;
        }
        public void RegisterExpectedRemoteRun(
            string threadId,
            string runId,
            ExecutionDevice target,
            DateTimeOffset startedAt)
        {
            var thread = Profile.OwnThreads.Single(item => item.Id == threadId);
            thread.ExecutionRunId = runId;
            RegisteredRemoteRuns++;
        }

        public void ClearRemoteRunProjection(
            string threadId,
            string? runId = null,
            DateTimeOffset? clearedAt = null)
        {
            Profile.OwnThreads.Single(item => item.Id == threadId).ExecutionRunId = null;
            ClearedRemoteRuns++;
        }

        public void TrackQueuedTopicRun(
            string threadId,
            string runId,
            string lineId,
            TopicQueueStage stage = TopicQueueStage.Sending)
            => QueuedRuns.MarkWaiting(threadId, runId, lineId, stage);
        public void SetQueuedTopicRunStage(string threadId, string runId, TopicQueueStage stage)
            => QueuedRuns.SetStage(threadId, runId, stage);
        public void StartQueuedTopicRun(string threadId, string runId)
            => QueuedRuns.MarkStarted(threadId, runId);
        public void CompleteQueuedTopicRun(string threadId, string runId)
            => QueuedRuns.Complete(threadId, runId);
        public bool IsKnownQueuedTopicRun(string threadId, string runId)
            => QueuedRuns.IsKnownRun(threadId, runId);
        public int QueuedCountForThread(string threadId) => QueuedRuns.WaitingCount(threadId);
        public bool IsLineQueued(string lineId) => QueuedRuns.IsLineWaiting(lineId);
        public bool IsQueuedTopicRunLine(string threadId, string runId, string lineId)
        {
            var queued = QueuedRuns.FindByLine(threadId, lineId);
            return queued is { Waiting: true }
                   && queued.ThreadId == threadId
                   && queued.RunId == runId;
        }
        public bool RemoveCancelledQueuedTopicLine(string threadId, string runId, string lineId)
        {
            var thread = Profile.OwnThreads.Single(item => item.Id == threadId);
            var removed = thread.Lines.RemoveAll(line =>
                line.Id == lineId || line.ReplyToLineId == lineId) > 0;
            QueuedRuns.Complete(threadId, runId);
            return removed;
        }

        public void ApplyRemoteRunUpdate(TopicRunUpdatePayload update)
            => _ = TryApplyRemoteRunUpdate(update);

        public bool TryApplyRemoteRunUpdate(TopicRunUpdatePayload update)
        {
            if (!RemoteRunUpdatePersistenceSucceeds) return false;
            if (update.Phase == TopicRunPhase.Queued)
            {
                if (update.Queued > 0 && update.TriggerLineId is not null)
                    TrackQueuedTopicRun(update.ThreadId, update.RunId, update.TriggerLineId);
            }
            else if (update.Phase is TopicRunPhase.Completed or TopicRunPhase.Failed or TopicRunPhase.Cancelled)
                CompleteQueuedTopicRun(update.ThreadId, update.RunId);
            else
                StartQueuedTopicRun(update.ThreadId, update.RunId);
            return true;
        }
        public void SetAgentRun(AgentRunState run)
            => Profile.OwnThreads.Single(item => item.Id == run.ThreadId).ExecutionRunId = run.RunId;

        public bool CancelThreadTurn(string threadId) => true;
        public bool IsThreadBusy(string threadId) => Busy;

        public CancellationToken BeginThreadTurn(string threadId, bool building)
        {
            Busy = true;
            return CancellationToken.None;
        }

        public void ClearThreadBuilding(string threadId) { }
        public void MarkThreadCompleted(string threadId) { }
        public void UpdateAgentRun(string threadId, AgentRunPhase phase) { }
        public void EndThreadTurn(string threadId) => Busy = false;
        public void Mutate(Action<MeshProfile> change) => change(Profile);
    }

    public sealed class AgentService
    {
        public Func<string, string, CancellationToken, Task<string>> Continue { get; set; } =
            static (_, _, _) => Task.FromResult("");

        public Action<IProgress<AgentDelta>?>? Stream { get; set; }

        public Task<string> ContinueAsOwnerAsync(
            string threadId,
            string triggerLineId,
            string runId,
            DateTimeOffset startedAt,
            IProgress<AgentRunState>? runProgress,
            IProgress<AgentStep>? stepProgress,
            IProgress<AgentDelta>? deltaProgress,
            CancellationToken cancellationToken = default)
        {
            Stream?.Invoke(deltaProgress);
            return Continue(threadId, runId, cancellationToken);
        }

        public Task<string> BuildWidgetAsync(
            string description,
            CancellationToken cancellationToken = default)
            => Task.FromResult("```html-app\n<html></html>\n```");

        public Task<string> GenerateWidgetNameAsync(
            string description,
            CancellationToken cancellationToken = default)
            => Task.FromResult("Test Widget");

        public Task<string> RefineWidgetAsync(
            string canonicalPrompt,
            string changeRequest,
            CancellationToken cancellationToken = default)
            => BuildWidgetAsync(changeRequest, cancellationToken);
    }

    public sealed record MessageSegment(bool IsApp, string Content);

    public static class Markdown
    {
        public static IReadOnlyList<MessageSegment> Parse(string text)
        {
            const string open = "```html-app\n";
            var start = text.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return [new MessageSegment(false, text)];
            start += open.Length;
            var end = text.IndexOf("\n```", start, StringComparison.Ordinal);
            return
            [
                new MessageSegment(
                    true,
                    end < 0 ? text[start..] : text[start..end])
            ];
        }
    }
}

namespace Mesh.App.Tests
{
    [TestClass]
    public sealed class DeviceTopicExecutionTests
    {
        [TestMethod]
        public async Task LocalSubmission_IsIdempotentAndAppendsOneTriggerLine()
        {
            var state = StateWithThread();
            var runner = new RecordingRunner();
            var transport = new RecordingTransport();
            var router = new TopicExecutionRouter(state, runner, transport);
            var draft = Draft();

            var first = await router.SubmitAsync(draft, null, CancellationToken.None);
            var second = await router.SubmitAsync(draft, null, CancellationToken.None);
            await runner.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(first.Accepted);
            Assert.IsTrue(second.Accepted);
            Assert.AreEqual(1, runner.Calls);
            Assert.AreEqual(1, state.Profile.OwnThreads[0].Lines.Count);
            Assert.AreEqual(draft.TriggerLineId, state.Profile.OwnThreads[0].Lines[0].Id);
            Assert.AreEqual(0, transport.Dispatches);
        }

        [TestMethod]
        public async Task RemoteSubmission_ReusesOptimisticTriggerLine()
        {
            var state = StateWithThread();
            var runner = new RecordingRunner();
            var transport = new RecordingTransport
            {
                Devices =
                [
                    new DeviceInfo("target", "Workstation", true, DevicePlatforms.Windows, true)
                ]
            };
            var router = new TopicExecutionRouter(state, runner, transport);
            var draft = Draft() with { TargetDeviceId = "target" };
            state.AddOwnChatLine(draft.ThreadId, new ChatLine
            {
                Id = draft.TriggerLineId,
                Role = "user",
                Text = draft.Prompt,
                At = draft.TriggerAt
            });

            var result = await router.SubmitAsync(draft, null, CancellationToken.None);

            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(1, transport.Dispatches);
            Assert.AreEqual(1, state.Profile.OwnThreads[0].Lines.Count);
            Assert.AreEqual(draft.TriggerLineId, state.Profile.OwnThreads[0].Lines[0].Id);
        }

        [TestMethod]
        public async Task RemoteSubmission_DoesNotDispatchWhenQueuedStateCannotPersist()
        {
            var state = StateWithThread();
            state.RemoteRunUpdatePersistenceSucceeds = false;
            var transport = new RecordingTransport
            {
                Devices =
                [
                    new DeviceInfo("target", "Workstation", true, DevicePlatforms.Windows, true)
                ]
            };
            var router = new TopicExecutionRouter(state, new RecordingRunner(), transport);
            var draft = Draft() with { TargetDeviceId = "target" };

            var result = await router.SubmitAsync(draft, null, CancellationToken.None);

            Assert.IsFalse(result.Accepted);
            Assert.AreEqual("dispatch_failed", result.Code);
            Assert.AreEqual(0, transport.Dispatches);
        }

        [TestMethod]
        public async Task RemoteSubmission_BindsBuildsManifestAndRegistersProjection()
        {
            var state = StateWithThread();
            var runner = new RecordingRunner();
            var transport = new RecordingTransport
            {
                Devices =
                [
                    new DeviceInfo("offline", "Offline", false, DevicePlatforms.Windows, true),
                    new DeviceInfo("target", "Workstation", true, DevicePlatforms.Windows, true),
                    new DeviceInfo("not-ready", "Tablet", true, DevicePlatforms.Android, false)
                ]
            };
            var router = new TopicExecutionRouter(state, runner, transport);
            var attachment = new ChatAttachment("notes.txt", "text/plain", [1, 2, 3]);

            var draft = Draft() with
            {
                TargetDeviceId = "target",
                Attachments = [attachment]
            };

            var result = await router.SubmitAsync(draft, null, CancellationToken.None);
            var listed = await router.ListEligibleDevicesAsync(CancellationToken.None);

            Assert.IsTrue(result.Accepted);
            Assert.AreEqual("target", state.Profile.OwnThreads[0].ExecutionDeviceId);
            Assert.AreEqual(1, state.RegisteredRemoteRuns);
            Assert.AreEqual(1, transport.Dispatches);
            Assert.AreEqual("target", transport.Request!.TargetDeviceId);
            Assert.AreEqual(1, transport.Request.Attachments!.Count);
            Assert.AreEqual(
                transport.Request.Attachments[0].Id,
                transport.Request.AttachmentIds![0]);
            Assert.AreEqual(3L, transport.Request.Attachments[0].Length);
            Assert.AreEqual(3, listed.Count);
            Assert.AreEqual("offline", listed[1].DeviceId);
            Assert.AreEqual("target", listed[2].DeviceId);
            Assert.AreEqual(0, state.Profile.OwnThreads[0].Lines[0].Attachments.Count);
        }

        [TestMethod]
        public async Task RemoteSubmission_ToOfflineBoundDeviceIsQueued()
        {
            var state = StateWithThread();
            var thread = state.Profile.OwnThreads[0];
            thread.ExecutionDeviceId = "offline";
            thread.ExecutionDeviceName = "Laptop";
            thread.ExecutionDevicePlatform = DevicePlatforms.Windows;
            var transport = new RecordingTransport
            {
                Devices = [new DeviceInfo("offline", "Laptop", false, DevicePlatforms.Windows, true)],
                ResultCode = DurableDeliveryCodes.LocalQueued
            };
            var router = new TopicExecutionRouter(state, new RecordingRunner(), transport);
            var draft = Draft() with { TargetDeviceId = "offline" };

            var result = await router.SubmitAsync(draft, null, CancellationToken.None);

            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(DurableDeliveryCodes.LocalQueued, result.Code);
            Assert.AreEqual(1, transport.Dispatches);
            Assert.IsTrue(state.IsLineQueued(draft.TriggerLineId));
            Assert.AreEqual(TopicQueueStage.Sending, state.QueuedRuns.FindByLine(draft.TriggerLineId)!.Stage);
        }
        [TestMethod]
        public async Task CancellingQueuedSubmission_KeepsPromptUntilTerminalUpdate()
        {
            var state = StateWithThread();
            var thread = state.Profile.OwnThreads[0];
            thread.ExecutionDeviceId = "offline";
            thread.ExecutionDeviceName = "Laptop";
            thread.ExecutionDevicePlatform = DevicePlatforms.Windows;
            var transport = new RecordingTransport
            {
                Devices = [new DeviceInfo("offline", "Laptop", false, DevicePlatforms.Windows, true)],
                ResultCode = DurableDeliveryCodes.LocalQueued
            };
            var router = new TopicExecutionRouter(state, new RecordingRunner(), transport);
            var draft = Draft() with { TargetDeviceId = "offline" };
            Assert.IsTrue((await router.SubmitAsync(draft, null, CancellationToken.None)).Accepted);

            var cancelled = await router.CancelQueuedAsync(
                draft.ThreadId, draft.RunId, draft.TriggerLineId, CancellationToken.None);

            Assert.IsTrue(cancelled);
            Assert.AreEqual(1, transport.Cancellations);
            Assert.AreEqual(1, thread.Lines.Count);
            Assert.IsTrue(state.IsLineQueued(draft.TriggerLineId));
            Assert.AreEqual(
                TopicQueueStage.Cancelling,
                state.QueuedRuns.FindByLine(draft.TriggerLineId)!.Stage);
        }

        [TestMethod]
        public async Task RejectedQueuedCancellation_DoesNotClaimCancellationIsPending()
        {
            var state = StateWithThread();
            var thread = state.Profile.OwnThreads[0];
            thread.ExecutionDeviceId = "offline";
            thread.ExecutionDeviceName = "Laptop";
            thread.ExecutionDevicePlatform = DevicePlatforms.Windows;
            var transport = new RecordingTransport
            {
                Devices = [new DeviceInfo("offline", "Laptop", false, DevicePlatforms.Windows, true)],
                ResultCode = DurableDeliveryCodes.LocalQueued,
                CancellationAccepted = false
            };
            var router = new TopicExecutionRouter(state, new RecordingRunner(), transport);
            var draft = Draft() with { TargetDeviceId = "offline" };
            Assert.IsTrue((await router.SubmitAsync(draft, null, CancellationToken.None)).Accepted);

            var cancelled = await router.CancelQueuedAsync(
                draft.ThreadId, draft.RunId, draft.TriggerLineId, CancellationToken.None);

            Assert.IsFalse(cancelled);
            Assert.AreEqual(1, transport.Cancellations);
            Assert.AreEqual(
                TopicQueueStage.Sending,
                state.QueuedRuns.FindByLine(draft.TriggerLineId)!.Stage);
        }
        [TestMethod]
        public async Task EligibleDeviceRefreshes_AreSerializedSoThePickerGetsTheNewestResult()
        {
            var state = StateWithThread();
            var transport = new SequencedDeviceTransport();
            var router = new TopicExecutionRouter(state, new RecordingRunner(), transport);

            var startupLoad = router.ListEligibleDevicesAsync(CancellationToken.None);
            await transport.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var pickerLoad = router.ListEligibleDevicesAsync(CancellationToken.None);

            Assert.AreEqual(1, transport.Calls, "the picker refresh must wait behind the startup refresh");
            transport.FirstResult.SetResult([]);
            var startupDevices = await startupLoad;
            await transport.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.SecondResult.SetResult(
            [
                new DeviceInfo("desktop", "Desktop", true, DevicePlatforms.Windows, true)
            ]);
            var pickerDevices = await pickerLoad;

            Assert.AreEqual(1, startupDevices.Count);
            Assert.AreEqual(2, pickerDevices.Count);
            Assert.AreEqual("desktop", pickerDevices[1].DeviceId);
        }

        [TestMethod]
        public async Task SubmissionBehindActiveRun_MarksTriggerLineQueued()
        {
            var state = StateWithThread();
            var thread = state.Profile.OwnThreads[0];
            thread.ExecutionDeviceId = "target";
            thread.ExecutionRunId = "active-run";
            var transport = new RecordingTransport
            {
                Devices = [new DeviceInfo("target", "Workstation", true, DevicePlatforms.Windows, true)]
            };
            var router = new TopicExecutionRouter(state, new RecordingRunner(), transport);
            var progress = new RecordingProgress();
            var draft = Draft() with
            {
                RunId = "queued-run",
                TriggerLineId = "queued-line",
                TargetDeviceId = "target"
            };

            var result = await router.SubmitAsync(draft, progress, CancellationToken.None);

            Assert.IsTrue(result.Accepted);
            Assert.IsTrue(state.IsLineQueued("queued-line"));
            var queued = progress.Updates.Single(update => update.Phase == TopicRunPhase.Queued);
            Assert.AreEqual(1, queued.Queued);
            Assert.AreEqual("queued-line", queued.TriggerLineId);
        }

        [TestMethod]
        public async Task ValidationAndRunCorrelation_RejectBeforeMutation()
        {
            var state = StateWithThread();
            var runner = new RecordingRunner();
            var transport = new RecordingTransport();
            var router = new TopicExecutionRouter(state, runner, transport);
            var invalid = Draft() with { WidgetContext = "{not-json" };

            var rejected = await router.SubmitAsync(invalid, null, CancellationToken.None);
            var accepted = await router.SubmitAsync(Draft(), null, CancellationToken.None);
            var conflict = await router.SubmitAsync(
                Draft() with { Prompt = "different" }, null, CancellationToken.None);
            var wrongStop = await router.StopAsync(
                "other-thread", Draft().RunId, CancellationToken.None);

            Assert.IsFalse(rejected.Accepted);
            Assert.AreEqual("invalid_widget_context", rejected.Code);
            Assert.AreEqual(1, state.Profile.OwnThreads[0].Lines.Count);
            Assert.IsTrue(accepted.Accepted);
            Assert.IsFalse(conflict.Accepted);
            Assert.AreEqual("run_id_conflict", conflict.Code);
            Assert.IsFalse(wrongStop);
        }

        [TestMethod]
        public async Task Runner_IsFifoPerTopicAndClearsOnlyTransientAttachments()
        {
            var state = StateWithThread();
            var thread = state.Profile.OwnThreads[0];
            thread.Lines.AddRange(
            [
                new ChatLine { Id = "line-1", Role = "user", Text = "first" },
                new ChatLine { Id = "line-2", Role = "user", Text = "second" }
            ]);
            var firstStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var dequeued = new List<string>();
            var agent = new AgentService
            {
                Continue = async (_, runId, cancellationToken) =>
                {
                    lock (order) order.Add(runId);
                    if (runId == "run-1")
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(cancellationToken);
                    }
                    return "";
                }
            };
            var runner = new TopicTurnRunner(agent, state);
            var firstProgress = new RecordingProgress();
            var secondProgress = new RecordingProgress();
            var at = new DateTimeOffset(2026, 7, 21, 16, 0, 0, TimeSpan.Zero);
            var first = new TopicTurnDraft(
                "run-1", "thread-1", "line-1", "owner", "first", at,
                TopicTurnMode.Single,
                Attachments: [new ChatAttachment("one.txt", "text/plain", [1])]);
            var second = new TopicTurnDraft(
                "run-2", "thread-1", "line-2", "owner", "second", at.AddSeconds(1),
                TopicTurnMode.Single,
                Attachments: [new ChatAttachment("two.txt", "text/plain", [2])]);

            var firstTask = runner.ExecuteAsync(
                first,
                firstProgress,
                CancellationToken.None,
                _ =>
                {
                    dequeued.Add(first.RunId);
                    return Task.CompletedTask;
                });
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var secondTask = runner.ExecuteAsync(
                second,
                secondProgress,
                CancellationToken.None,
                _ =>
                {
                    dequeued.Add(second.RunId);
                    return Task.CompletedTask;
                });
            await secondProgress.Queued.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(state.IsLineQueued("line-2"));
            Assert.AreEqual("line-2", secondProgress.Updates[0].TriggerLineId);

            CollectionAssert.AreEqual(new[] { "run-1" }, order);
            CollectionAssert.AreEqual(new[] { "run-1" }, dequeued);
            releaseFirst.TrySetResult();
            await Task.WhenAll(firstTask, secondTask);

            CollectionAssert.AreEqual(new[] { "run-1", "run-2" }, order);
            CollectionAssert.AreEqual(new[] { "run-1", "run-2" }, dequeued);
            Assert.AreEqual(0, thread.Lines[0].Attachments.Count);
            Assert.AreEqual(0, thread.Lines[1].Attachments.Count);
            Assert.IsTrue(firstProgress.Updates.All(update =>
                update.RunId == "run-1" && update.ThreadId == "thread-1"));
            Assert.IsTrue(secondProgress.Updates.All(update =>
                update.RunId == "run-2" && update.ThreadId == "thread-1"));
            Assert.IsFalse(state.IsLineQueued("line-2"));
            Assert.IsTrue(secondProgress.Updates.Any(update =>
                update.Phase == TopicRunPhase.Executing && update.Status == "Running"));
        }

        [TestMethod]
        public async Task Runner_ForwardsCoalescedStreamingDeltas()
        {
            var state = StateWithThread();
            var thread = state.Profile.OwnThreads[0];
            thread.Lines.Add(new ChatLine { Id = "line-1", Role = "user", Text = "hi" });
            var agent = new AgentService
            {
                Stream = delta =>
                {
                    for (var i = 0; i < 120; i++)
                        delta?.Report(new AgentDelta(AgentDeltaKind.Reasoning, "x"));
                    for (var i = 0; i < 120; i++)
                        delta?.Report(new AgentDelta(AgentDeltaKind.Answer, "y"));
                }
            };
            var runner = new TopicTurnRunner(agent, state);
            var progress = new RecordingProgress();
            var at = new DateTimeOffset(2026, 7, 21, 16, 0, 0, TimeSpan.Zero);
            var draft = new TopicTurnDraft(
                "run-1", "thread-1", "line-1", "owner", "hi", at, TopicTurnMode.Single);

            await runner.ExecuteAsync(draft, progress, CancellationToken.None);

            var deltas = progress.Updates.Where(update => update.Delta is not null).ToList();
            Assert.IsTrue(deltas.Count >= 4, "the stream should be coalesced into several fragments");
            Assert.IsTrue(deltas.Count < 240, "coalescing should send far fewer envelopes than tokens");
            for (var i = 1; i < deltas.Count; i++)
                Assert.IsTrue(
                    deltas[i].DeltaSeq > deltas[i - 1].DeltaSeq,
                    "delta sequence numbers must be strictly increasing");
            Assert.IsTrue(deltas.All(update =>
                update.DeltaKind is not null
                && update.RunId == "run-1"
                && update.ThreadId == "thread-1"));
            Assert.IsTrue(
                deltas.All(update =>
                    update.Delta!.All(character => character == 'x')
                    || update.Delta!.All(character => character == 'y')),
                "reasoning and answer must never be mixed inside one fragment");
            var reasoning = string.Concat(deltas
                .Where(update => update.DeltaKind == TopicRunDeltaKind.Reasoning)
                .Select(update => update.Delta));
            var answer = string.Concat(deltas
                .Where(update => update.DeltaKind == TopicRunDeltaKind.Answer)
                .Select(update => update.Delta));
            Assert.AreEqual(new string('x', 120), reasoning);
            Assert.AreEqual(new string('y', 120), answer);
        }

        private static Mesh.App.Services.AppState StateWithThread()
        {
            var state = new Mesh.App.Services.AppState();
            state.Profile.Handle = "owner";
            state.Profile.Model.ApiKey = "test";
            state.Profile.OwnThreads.Add(new OwnThread
            {
                Id = "thread-1",
                Title = "Topic"
            });
            return state;
        }

        private static TopicTurnDraft Draft() => new(
            "run-1",
            "thread-1",
            "line-1",
            "owner",
            "Do the work",
            new DateTimeOffset(2026, 7, 21, 16, 0, 0, TimeSpan.Zero),
            TopicTurnMode.Single);

        private sealed class RecordingRunner : ITopicTurnRunner
        {
            public int Calls { get; private set; }
            public TaskCompletionSource Called { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<TopicRunCompletion> ExecuteAsync(
                TopicTurnDraft draft,
                IProgress<TopicRunUpdatePayload> progress,
                CancellationToken cancellationToken,
                Func<CancellationToken, Task>? onStarted = null)
            {
                Calls++;
                Called.TrySetResult();
                return Task.FromResult(new TopicRunCompletion(
                    draft.RunId,
                    draft.ThreadId,
                    TopicRunPhase.Completed,
                    DateTimeOffset.UtcNow));
            }
        }

        private sealed class RecordingTransport : IDeviceTopicTransport
        {
            public IReadOnlyList<DeviceInfo> Devices { get; set; } = [];
            public int Dispatches { get; private set; }
            public int Cancellations { get; private set; }
            public TopicRunRequestPayload? Request { get; private set; }
            public string ResultCode { get; set; } = "accepted";
            public bool CancellationAccepted { get; set; } = true;

            public Task<TopicDispatchResult> DispatchAsync(
                string targetDeviceId,
                TopicRunRequestPayload request,
                IReadOnlyList<ChatAttachment> attachments,
                CancellationToken cancellationToken)
            {
                Dispatches++;
                Request = request;
                return Task.FromResult(TopicDispatchResult.Ok(request.RunId, ResultCode));
            }

            public Task<bool> CancelAsync(
                string targetDeviceId,
                TopicRunCancelPayload cancel,
                CancellationToken cancellationToken)
            {
                Cancellations++;
                return Task.FromResult(CancellationAccepted);
            }

            public Task<IReadOnlyList<DeviceInfo>> ListEligibleDevicesAsync(
                CancellationToken cancellationToken)
                => Task.FromResult(Devices);
        }

        private sealed class SequencedDeviceTransport : IDeviceTopicTransport
        {
            public int Calls { get; private set; }
            public TaskCompletionSource FirstStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource SecondStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<IReadOnlyList<DeviceInfo>> FirstResult { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<IReadOnlyList<DeviceInfo>> SecondResult { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<TopicDispatchResult> DispatchAsync(
                string targetDeviceId,
                TopicRunRequestPayload request,
                IReadOnlyList<ChatAttachment> attachments,
                CancellationToken cancellationToken)
                => Task.FromResult(TopicDispatchResult.Ok(request.RunId));

            public Task<bool> CancelAsync(
                string targetDeviceId,
                TopicRunCancelPayload cancel,
                CancellationToken cancellationToken)
                => Task.FromResult(true);

            public Task<IReadOnlyList<DeviceInfo>> ListEligibleDevicesAsync(
                CancellationToken cancellationToken)
            {
                Calls++;
                if (Calls == 1) { FirstStarted.TrySetResult(); return FirstResult.Task; }
                SecondStarted.TrySetResult();
                return SecondResult.Task;
            }
        }

        private sealed class RecordingProgress : IProgress<TopicRunUpdatePayload>
        {
            public List<TopicRunUpdatePayload> Updates { get; } = [];
            public TaskCompletionSource Queued { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Report(TopicRunUpdatePayload value)
            {
                lock (Updates) Updates.Add(value);
                if (value.Phase == TopicRunPhase.Queued) Queued.TrySetResult();
            }
        }
    }
}
#endif
