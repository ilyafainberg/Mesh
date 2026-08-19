#if ANDROID
using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using Mesh.App.Services;

namespace Mesh.App.Platforms.Android;

[Register("net/meshrelay/mesh/MeshReplicationSyncWorker")]
public sealed class MeshReplicationSyncWorker : Worker
{
    private const string WakeIdKey = "mesh_wake_id";
    private const string VisibleAlertKey = "mesh_visible_alert";
    private CancellationTokenSource? activeBudget;
    public MeshReplicationSyncWorker(Context context, WorkerParameters workerParameters)
        : base(context, workerParameters)
    {
    }

    public override ListenableWorker.Result DoWork()
    {
        using var wakeSession = NotificationWakeSessionBridge.Begin(
            InputData.GetString(WakeIdKey),
            InputData.GetBoolean(VisibleAlertKey, false));
        using var budget = new CancellationTokenSource(OnlineReplicationWakeCoordinator.DefaultBudget);
        if (Interlocked.CompareExchange(ref activeBudget, budget, null) is not null)
            throw new InvalidOperationException("The replication worker is already running.");

        try
        {
            var result = OnlineReplicationWakeBridge
                .SynchronizePendingAsync(OnlineReplicationWakeCoordinator.DefaultBudget, budget.Token)
                .GetAwaiter()
                .GetResult();
            return result.Outcome == OnlineReplicationWakeOutcome.Failed
                ? ListenableWorker.Result.InvokeRetry()!
                : ListenableWorker.Result.InvokeSuccess()!;
        }
        catch (OperationCanceledException)
        {
            return ListenableWorker.Result.InvokeRetry()!;
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("android-replication-worker", ex);
            return ListenableWorker.Result.InvokeFailure()!;
        }
        finally
        {
            Interlocked.CompareExchange(ref activeBudget, null, budget);
        }
    }

    public override void OnStopped()
    {
        Interlocked.Exchange(ref activeBudget, null)?.Cancel();
        base.OnStopped();
    }

    public static void Enqueue(Context context, string? wakeId = null, bool visibleAlert = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var constraints = new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.Connected!)
            .Build();
        var input = new Data.Builder()
            .PutString(WakeIdKey, wakeId)
            .PutBoolean(VisibleAlertKey, visibleAlert)
            .Build();
        var builtRequest = new OneTimeWorkRequest.Builder(typeof(MeshReplicationSyncWorker))
            .SetConstraints(constraints)
            .SetExpedited(OutOfQuotaPolicy.RunAsNonExpeditedWorkRequest!)
            .SetInputData(input)
            .Build();
        if (builtRequest is not OneTimeWorkRequest request)
            throw new InvalidOperationException("WorkManager returned an unexpected request type.");

        WorkManager.GetInstance(context).EnqueueUniqueWork(
            AndroidReplicationWakePolicy.WorkName(wakeId),
            ExistingWorkPolicy.Keep!,
            request);
    }
}
#endif