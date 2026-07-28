using Mesh.Shared;

namespace Mesh.App.Services;

public sealed partial class AppState
{
    public bool SaveDeviceSyncSnapshotManifest(DeviceSyncSnapshotManifest manifest)
    {
        lock (profileSyncGate)
            return activeDb?.SaveDeviceSyncSnapshotManifest(manifest) == true;
    }

    public bool SaveDeviceSyncSnapshotChunk(DeviceSyncSnapshotChunk chunk)
    {
        lock (profileSyncGate)
            return activeDb?.SaveDeviceSyncSnapshotChunk(chunk) == true;
    }

    public MeshDb.DeviceSyncSnapshotTransferState? GetDeviceSyncSnapshotTransfer(string snapshotId)
    {
        lock (profileSyncGate)
            return activeDb?.GetDeviceSyncSnapshotTransfer(snapshotId);
    }

    public bool DeleteDeviceSyncSnapshotTransfer(string snapshotId)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.DeleteDeviceSyncSnapshotTransfer(snapshotId);
            return true;
        }
    }
    public MeshDb.DeviceSyncSnapshotResumeState GetDeviceSyncSnapshotResumeState(
        string sourceDeviceId,
        string targetDeviceId)
    {
        lock (profileSyncGate)
            return activeDb?.GetDeviceSyncSnapshotResumeState(sourceDeviceId, targetDeviceId)
                   ?? new MeshDb.DeviceSyncSnapshotResumeState(null, []);
    }

    public bool HasDeviceSyncSnapshotCompletion(
        string snapshotId,
        string sourceDeviceId,
        string targetDeviceId)
    {
        lock (profileSyncGate)
            return activeDb?.HasDeviceSyncSnapshotCompletion(
                snapshotId, sourceDeviceId, targetDeviceId) == true;
    }
    public bool RecordDeviceSyncSnapshotCompletion(DeviceSyncSnapshotComplete completion)
    {
        lock (profileSyncGate)
        {
            if (activeDb is null) return false;
            activeDb.RecordDeviceSyncSnapshotCompletion(completion);
            return true;
        }
    }
}
