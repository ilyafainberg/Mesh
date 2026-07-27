using System.Diagnostics;
using System.Text.Json;
using Mesh.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace Mesh.App.Services;

public sealed partial class MeshClient
{
    private static readonly TimeSpan BackgroundAuthenticationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BackgroundIdlePeriod = TimeSpan.FromMilliseconds(750);
    private readonly SemaphoreSlim backgroundSyncGate = new(1, 1);

    private enum InboundDisposition
    {
        Acknowledge,
        Defer
    }

    private async Task<InboundDisposition> ProcessInboundAsync(
        MeshEnvelope envelope,
        InboundProcessingMode mode,
        DeviceSyncIdentity? identity,
        bool sessionSupportsDeviceSync,
        CancellationToken ct)
    {
        if (mode == InboundProcessingMode.Background
            && BackgroundInboundPolicy.RequiresForeground(envelope.Kind))
            return InboundDisposition.Defer;

        await HandleInboundAsync(envelope, mode, identity, sessionSupportsDeviceSync, ct);
        return InboundDisposition.Acknowledge;
    }

    public async Task<BackgroundSyncResult> SynchronizePendingAsync(CancellationToken ct = default)
    {
        if (lifecycle.IsForeground)
        {
            ResumeTransport();
            return BackgroundSyncResult.NoData();
        }

        await backgroundSyncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunBackgroundSyncSessionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            backgroundSyncGate.Release();
        }
    }

    private async Task<BackgroundSyncResult> RunBackgroundSyncSessionAsync(CancellationToken ct)
    {
        var profile = state.Profile;
        var normalizedHandle = AppState.Norm(profile.Handle);
        if (string.IsNullOrWhiteSpace(normalizedHandle)
            || string.IsNullOrWhiteSpace(profile.RelayUrl))
            return BackgroundSyncResult.NoData();
        if (string.IsNullOrWhiteSpace(profile.PublicKey)
            || string.IsNullOrWhiteSpace(profile.PrivateKey))
            return BackgroundSyncResult.Failed("identity_unavailable");

        var startedAt = Stopwatch.GetTimestamp();
        RuntimeDiagnostics.Current?.RecordEvent("background-sync", "starting");
        RelayCapabilities capabilities;
        try
        {
            capabilities = await ReadRelayCapabilitiesAsync(profile.RelayUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeDiagnostics.Current?.RecordException("background-sync-capabilities", ex);
            return BackgroundSyncResult.Failed("transport_unavailable");
        }
        if (!capabilities.DurableDelivery || !capabilities.BackgroundSync)
            return BackgroundSyncResult.Failed("background_sync_unsupported");

        var url = $"{profile.RelayUrl.TrimEnd('/')}{MeshHubProtocol.Route}"
                  + $"?handle={Uri.EscapeDataString(normalizedHandle)}&deliveryAck=1&backgroundSync=1";
        var connection = new HubConnectionBuilder().WithUrl(url).Build();
        var identity = new DeviceSyncIdentity(
            connection,
            profile.Handle,
            normalizedHandle,
            DeviceProtocol.DeviceId(profile.PublicKey),
            profile.PublicKey,
            profile.PrivateKey,
            profile.RelayUrl.TrimEnd('/'));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inboundGate = new SemaphoreSlim(1, 1);
        var lastActivity = Stopwatch.GetTimestamp();
        var activeHandlers = 0;
        var processed = 0;
        var deferred = 0;
        Exception? processingFailure = null;

        void Touch() => Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());

        connection.On<string>(MeshHubProtocol.Challenge, async nonce =>
        {
            try
            {
                var signature = IdentityService.Sign(profile.PrivateKey, nonce);
                await connection.InvokeAsync(
                    MeshHubProtocol.Authenticate, profile.PublicKey, signature, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        });
        connection.On(MeshHubProtocol.Ready, () =>
        {
            Touch();
            ready.TrySetResult();
        });
        connection.On<string>(MeshHubProtocol.Receive, async envelopeJson =>
        {
            Interlocked.Increment(ref activeHandlers);
            Touch();
            try
            {
                await inboundGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var envelope = JsonSerializer.Deserialize<MeshEnvelope>(envelopeJson, Json);
                    if (envelope is null) return;
                    var disposition = await ProcessInboundAsync(
                        envelope,
                        InboundProcessingMode.Background,
                        identity,
                        capabilities.DeviceSync,
                        ct).ConfigureAwait(false);
                    if (disposition == InboundDisposition.Defer)
                    {
                        Interlocked.Increment(ref deferred);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(envelope.RelayDeliveryId))
                        await AcknowledgeDeliveryAsync(connection, envelope, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref processed);
                }
                finally
                {
                    inboundGate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref processingFailure, ex, null);
                RuntimeDiagnostics.Current?.RecordException("background-sync-envelope", ex);
            }
            finally
            {
                Interlocked.Decrement(ref activeHandlers);
                Touch();
            }
        });

        try
        {
            await connection.StartAsync(ct).ConfigureAwait(false);
            using (var authentication = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                authentication.CancelAfter(BackgroundAuthenticationTimeout);
                await ready.Task.WaitAsync(authentication.Token).ConfigureAwait(false);
            }

            await WaitForBackgroundIdleAsync(
                () => Volatile.Read(ref lastActivity),
                () => Volatile.Read(ref activeHandlers),
                () => Volatile.Read(ref processingFailure),
                ct).ConfigureAwait(false);
            _ = await connection.InvokeAsync<int>(
                MeshHubProtocol.RequestPendingDeliveries, ct).ConfigureAwait(false);
            Touch();
            await WaitForBackgroundIdleAsync(
                () => Volatile.Read(ref lastActivity),
                () => Volatile.Read(ref activeHandlers),
                () => Volatile.Read(ref processingFailure),
                ct).ConfigureAwait(false);

            if (processingFailure is not null)
                return BackgroundSyncResult.Failed("persistence_failed", processed, deferred);
            var result = processed > 0
                ? BackgroundSyncResult.NewData(processed, deferred)
                : BackgroundSyncResult.NoData(deferred);
            RuntimeDiagnostics.Current?.RecordEvent(
                "background-sync",
                $"outcome={result.Outcome}; processed={processed}; deferred={deferred}; "
                + $"elapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0}");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("background-sync-session", ex);
            return BackgroundSyncResult.Failed("transport_unavailable", processed, deferred);
        }
        finally
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                RuntimeDiagnostics.Current?.RecordException("background-sync-dispose", ex);
            }
        }
    }

    private static async Task WaitForBackgroundIdleAsync(
        Func<long> readLastActivity,
        Func<int> readActiveHandlers,
        Func<Exception?> readFailure,
        CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (readFailure() is not null)
                return;

            var elapsed = Stopwatch.GetElapsedTime(readLastActivity());
            if (readActiveHandlers() == 0 && elapsed >= BackgroundIdlePeriod)
                return;

            var delay = elapsed < BackgroundIdlePeriod
                ? BackgroundIdlePeriod - elapsed
                : pollInterval;
            if (delay > pollInterval)
                delay = pollInterval;
            if (delay <= TimeSpan.Zero)
                delay = TimeSpan.FromMilliseconds(1);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
