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

    private void RehydrateDurableTopicState()
    {
        lock (profileSyncGate)
        {
            queuedTopicRuns.Clear();
            if (activeDb is null) return;
            activeDb.PruneInboundTopicRuns(DateTimeOffset.UtcNow - TopicTransportPolicy.DedupRetention);
            foreach (var item in activeDb.ListTopicOutbox())
            {
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
