using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class AppState
{
    public bool SaveTopicOutbox(MeshDb.TopicOutboxItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.UpsertTopicOutbox(item);
            return true;
        }
    }

    public MeshDb.TopicOutboxItem? GetTopicOutbox(string runId)
    {
        lock (profileSyncGate)
            return activeDb?.GetTopicOutbox(runId);
    }

    public IReadOnlyList<MeshDb.TopicOutboxItem> ListTopicOutbox()
    {
        lock (profileSyncGate)
            return activeDb?.ListTopicOutbox() ?? [];
    }

    public bool SetTopicOutboxState(string runId, string outboxState, string? error = null)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.SetTopicOutboxState(runId, outboxState, error);
            return true;
        }
    }

    public bool DeleteTopicOutbox(string runId)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.DeleteTopicOutbox(runId);
            return true;
        }
    }

    public bool TryAcceptInboundTopicRun(MeshDb.InboundTopicRunItem item)
    {
        lock (profileSyncGate)
            return activeDb?.TryAddInboundTopicRun(item) == true;
    }

    public MeshDb.InboundTopicRunItem? GetInboundTopicRun(string runId)
    {
        lock (profileSyncGate)
            return activeDb?.GetInboundTopicRun(runId);
    }

    public MeshDb.InboundTopicCancellationItem? GetInboundTopicCancellation(string runId)
    {
        lock (profileSyncGate)
            return activeDb?.GetInboundTopicCancellation(runId);
    }

    public bool SaveInboundTopicCancellation(MeshDb.InboundTopicCancellationItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            if (activeDb.TryAddInboundTopicCancellation(item)) return true;
            var existing = activeDb.GetInboundTopicCancellation(item.RunId);
            return existing is not null
                   && string.Equals(existing.SourceDeviceId, item.SourceDeviceId, StringComparison.Ordinal)
                   && string.Equals(existing.ThreadId, item.ThreadId, StringComparison.Ordinal)
                   && string.Equals(existing.TerminalUpdateJson, item.TerminalUpdateJson, StringComparison.Ordinal);
        }
    }

    public IReadOnlyList<MeshDb.InboundTopicRunItem> ListInboundTopicRuns(params string[] states)
    {
        lock (profileSyncGate)
            return activeDb?.ListInboundTopicRuns(states) ?? [];
    }

    public bool SetInboundTopicRunState(string runId, string runState)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            return activeDb.SetInboundTopicRunState(runId, runState);
        }
    }

    public bool SetInboundTopicRunTerminal(
        string runId,
        string runState,
        TopicRunUpdatePayload terminalUpdate)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            return activeDb.SetInboundTopicRunTerminal(runId, runState, terminalUpdate);
        }
    }
    public bool SetInboundTopicRunTerminalAndQueue(
        string runId,
        string runState,
        TopicRunUpdatePayload terminalUpdate,
        MeshDb.DeviceEnvelopeOutboxItem outbox)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            return activeDb.SetInboundTopicRunTerminalAndQueue(
                runId, runState, terminalUpdate, outbox);
        }
    }
    public bool SaveInboundRejection(MeshDb.InboundRejectionItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.UpsertInboundRejection(item);
            return true;
        }
    }
    public bool SaveDeviceEnvelopeOutbox(MeshDb.DeviceEnvelopeOutboxItem item)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.UpsertDeviceEnvelopeOutbox(item);
            return true;
        }
    }

    public IReadOnlyList<MeshDb.DeviceEnvelopeOutboxItem> ListDeviceEnvelopeOutbox()
    {
        lock (profileSyncGate)
            return activeDb?.ListDeviceEnvelopeOutbox() ?? [];
    }

    public bool DeleteDeviceEnvelopeOutbox(string envelopeId)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.DeleteDeviceEnvelopeOutbox(envelopeId);
            return true;
        }
    }

    private void RehydrateTopicExecutionState()
    {
        lock (profileSyncGate)
        {
            queuedTopicRuns.Clear();
            if (activeDb is null) return;
            var dedupCutoff = DateTimeOffset.UtcNow - TopicTransportPolicy.DedupRetention;
            activeDb.PruneInboundTopicRuns(dedupCutoff);
            activeDb.PruneInboundTopicCancellations(dedupCutoff);
            activeDb.PruneInboundRejections(dedupCutoff);
            foreach (var item in activeDb.ListTopicOutbox())
            {
                var thread = Profile.OwnThreads.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, item.ThreadId, StringComparison.Ordinal));
                if (thread is not null
                    && RemoteRunReconciliation.HasCommittedAnswer(
                        thread.Lines, item.TriggerLineId))
                {
                    if (string.Equals(
                            thread.ExecutionRunId, item.RunId, StringComparison.Ordinal))
                    {
                        if (!activeDb.CompleteOwnThreadRunAndDeleteTopicOutbox(
                                thread.Id,
                                item.RunId,
                                thread.ExecutionDeviceId,
                                thread.ExecutionDeviceName,
                                thread.ExecutionDevicePlatform,
                                thread.ExecutionAt,
                                thread.LastActivityAt ?? item.UpdatedAt))
                            continue;
                        thread.ExecutionRunId = null;
                    }
                    else
                    {
                        activeDb.DeleteTopicOutbox(item.RunId);
                    }
                    terminalRemoteRuns.Add(item.ThreadId + "\0" + item.RunId);
                    remoteDeltaSeq.Remove(item.ThreadId + "\0" + item.RunId);
                    continue;
                }
                var stage = item.State switch
                {
                    TopicOutboxStates.RelayQueued => TopicQueueStage.Relay,
                    TopicOutboxStates.DeviceQueued => TopicQueueStage.Device,
                    TopicOutboxStates.CancelPending => TopicQueueStage.Cancelling,
                    TopicOutboxStates.Expired => TopicQueueStage.Expired,
                    TopicOutboxStates.Failed => TopicQueueStage.Failed,
                    _ => TopicQueueStage.Sending
                };
                queuedTopicRuns.MarkWaiting(
                    item.ThreadId, item.RunId, item.TriggerLineId, stage);
                if (item.State == TopicOutboxStates.Running)
                    queuedTopicRuns.MarkStarted(item.ThreadId, item.RunId);
            }
        }
    }
}
