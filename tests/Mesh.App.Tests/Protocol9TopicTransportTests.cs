using System.Threading.Channels;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DeviceTopicTransportTests
{
    [TestMethod]
    public async Task DeliveryRetryLoop_CoalescesWakeStormAndCapsBackoffRate()
    {
        var time = new ManualTimerTimeProvider();
        var observed = Channel.CreateUnbounded<int>();
        var keepRunning = 1;
        var callCount = 0;
        var loop = new TopicDeliveryRetryLoop(
            time,
            TimeSpan.FromMilliseconds(20),
            _ =>
            {
                var count = Interlocked.Increment(ref callCount);
                observed.Writer.TryWrite(count);
                if (count == 4)
                {
                    Interlocked.Exchange(ref keepRunning, 0);
                }
                return Task.CompletedTask;
            },
            () => Volatile.Read(ref keepRunning) == 1,
            TimeSpan.FromMilliseconds(50));

        for (var index = 0; index < 100; index++)
        {
            loop.Schedule();
            loop.Wake();
        }

        Assert.AreEqual(1, await observed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await time.WaitForTimerCountAsync(2);
        time.Advance(TimeSpan.FromMilliseconds(39));
        Assert.AreEqual(1, loop.AttemptCount, "backoff must not fire before virtual time is due");
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(2, await observed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await time.WaitForTimerCountAsync(3);
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.AreEqual(3, await observed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await time.WaitForTimerCountAsync(4);
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.AreEqual(4, await observed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        loop.Stop();

        Assert.AreEqual(4, loop.AttemptCount);
        Assert.AreEqual(1, loop.WorkerStartCount);
        Console.WriteLine(
            "DETERMINISTIC_RETRY attempts=1,2,3,4 firstBackoffMs=40 cappedBackoffMs=50 workerStarts=1");
    }

    [TestMethod]
    public async Task DeliveryRetryLoop_RosterAndConnectionWakeStormUsesOneWorkerWithoutLosingWake()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var keepRunning = 1;
        var calls = 0;
        var loop = new TopicDeliveryRetryLoop(
            TimeProvider.System,
            TimeSpan.FromHours(1),
            async _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                    return;
                }
                Interlocked.Exchange(ref keepRunning, 0);
                secondCompleted.TrySetResult();
            },
            () => Volatile.Read(ref keepRunning) == 1);

        loop.Wake();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 100; index++)
            loop.Wake();
        releaseFirst.TrySetResult();

        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        loop.Stop();

        Assert.AreEqual(2, loop.AttemptCount);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1, loop.WorkerStartCount);
    }

    [TestMethod]
    public void TopicEnvelope_PreservesExactDeviceRoutingAndCiphertext()
    {
        var envelope = MeshEnvelope.Create(
            "owner",
            "owner",
            MeshKinds.TopicRunRequest,
            "enc:v1:ciphertext",
            "signature",
            fromDevice: "source-device",
            toDevice: "target-device");

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = JsonSerializer.Deserialize<MeshEnvelope>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsNotNull(parsed);
        Assert.AreEqual("source-device", parsed.FromDevice);
        Assert.AreEqual("target-device", parsed.ToDevice);
        Assert.AreEqual("enc:v1:ciphertext", parsed.Body);
        Assert.AreEqual(MeshKinds.TopicRunRequest, parsed.Kind);
    }

    [TestMethod]
    public void TopicResponseEnvelope_PreservesMetadataOnlyPushHint()
    {
        var envelope = MeshEnvelope.Create(
            "owner",
            "owner",
            MeshKinds.TopicRunUpdate,
            "enc:v1:ciphertext",
            "signature",
            fromDevice: "host-device",
            toDevice: "source-device",
            pushHint: PushHintProtocol.ForTopicRunPhase(TopicRunPhase.Completed));

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = JsonSerializer.Deserialize<MeshEnvelope>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(PushHintProtocol.TopicResponse, parsed.PushHint);
        Assert.IsTrue(PushHintProtocol.IsTopicResponse(parsed));
        Assert.IsNull(PushHintProtocol.ForTopicRunPhase(TopicRunPhase.Executing));
        Assert.IsNull(PushHintProtocol.ForTopicRunPhase(TopicRunPhase.Failed));
        Assert.IsNull(PushHintProtocol.ForTopicRunPhase(TopicRunPhase.Cancelled));
    }

    [TestMethod]
    public void AttachmentManifest_MapsIdsToTransientAttachmentsInOrder()
    {
        IReadOnlyList<ChatAttachment> attachments =
        [
            new("one.txt", "text/plain", [1, 2]),
            new("two.png", "image/png", [3])
        ];
        var ids = new[] { "attachment-one", "attachment-two" };
        var manifest = attachments.Select((item, index) =>
            new TopicRunAttachment(ids[index], item.Name, item.MimeType, item.Data.LongLength)).ToArray();
        var request = new TopicRunRequestPayload(
            "run", "thread", "line", "owner", "prompt", DateTimeOffset.UtcNow,
            "target-device", TopicTurnMode.Single, Attachments: manifest, AttachmentIds: ids);

        Assert.IsTrue(TopicRunProtocol.TryParseRequest(TopicRunProtocol.RequestBody(request), out var parsed));
        CollectionAssert.AreEqual(ids, parsed.AttachmentIds!.ToArray());
        Assert.AreEqual(attachments[0].Data.LongLength, parsed.Attachments![0].Length);
        Assert.AreEqual(attachments[1].MimeType, parsed.Attachments[1].MimeType);
    }

    [TestMethod]
    public void TopicKinds_AreDistinctFromLegacyHomeAgentKinds()
    {
        Assert.AreNotEqual(MeshKinds.RemoteAgentRequest, MeshKinds.TopicRunRequest);
        Assert.AreNotEqual(MeshKinds.RemoteAgentResponse, MeshKinds.TopicRunUpdate);
        Assert.AreEqual("attachment.chunk", MeshKinds.AttachmentChunk);
    }

    [TestMethod]
    public void Request_WithDuplicateAttachmentIds_IsRejected()
    {
        var attachments = new[]
        {
            new TopicRunAttachment("same", "one.txt", "text/plain", 1),
            new TopicRunAttachment("same", "two.txt", "text/plain", 1)
        };
        var request = new TopicRunRequestPayload(
            "run", "thread", "line", "owner", "prompt", DateTimeOffset.UtcNow,
            "target-device", TopicTurnMode.Single, Attachments: attachments);

        Assert.IsFalse(TopicRunProtocol.TryParseRequest(TopicRunProtocol.RequestBody(request), out _));
    }

    [TestMethod]
    public void Chunk_OverProtocolLimit_IsRejected()
    {
        var chunk = new AttachmentChunkPayload(
            "run",
            "attachment",
            0,
            1,
            new byte[AttachmentChunkProtocol.MaxChunkBytes + 1],
            "file.bin",
            "application/octet-stream");

        Assert.IsFalse(TopicRunProtocol.TryParseChunk(TopicRunProtocol.ChunkBody(chunk), out _));
    }

    private sealed class ManualTimerTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private readonly Channel<int> timerCounts = Channel.CreateUnbounded<int>();
        private long timestamp;
        private int timerCount;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref timestamp);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (gate) timers.Add(timer);
            timerCounts.Writer.TryWrite(Interlocked.Increment(ref timerCount));
            return timer;
        }

        public async Task WaitForTimerCountAsync(int expected)
        {
            while (Volatile.Read(ref timerCount) < expected)
                await timerCounts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref timestamp, elapsed.Ticks);
            while (true)
            {
                ManualTimer? due;
                lock (gate)
                    due = timers.FirstOrDefault(timer => timer.IsDue(timestamp));
                if (due is null) return;
                due.Fire(timestamp);
            }
        }

        private sealed class ManualTimer(
            ManualTimerTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private long dueAt = long.MaxValue;
            private long periodTicks = Timeout.InfiniteTimeSpan.Ticks;
            private int disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref disposed) != 0) return false;
                lock (owner.gate)
                {
                    dueAt = dueTime == Timeout.InfiniteTimeSpan
                        ? long.MaxValue
                        : checked(owner.timestamp + Math.Max(0, dueTime.Ticks));
                    periodTicks = period.Ticks;
                }
                return true;
            }

            public bool IsDue(long now)
                => Volatile.Read(ref disposed) == 0 && dueAt <= now;

            public void Fire(long now)
            {
                lock (owner.gate)
                {
                    if (!IsDue(now)) return;
                    dueAt = periodTicks > 0
                        ? checked(dueAt + periodTicks)
                        : long.MaxValue;
                }
                callback(state);
            }

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
