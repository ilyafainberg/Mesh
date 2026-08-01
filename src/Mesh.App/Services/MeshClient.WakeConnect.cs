using System.Diagnostics;
using System.Text.Json;
using Mesh.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace Mesh.App.Services;

public sealed partial class MeshClient
{
    private static readonly TimeSpan WakeAuthenticationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WakeIdlePeriod = TimeSpan.FromMilliseconds(750);
    private readonly SemaphoreSlim wakeConnectGate = new(1, 1);


    private sealed class InboundRetryException(string reason) : Exception(reason);
    private sealed class InboundPermanentRejectException(string reason, Exception? inner = null)
        : Exception(reason, inner);

    private async Task<InboundDisposition> ProcessInboundAsync(
        MeshEnvelope envelope,
        InboundProcessingMode mode,
        ReplicationConnectionIdentity? identity,
        bool sessionSupportsReplication,
        CancellationToken ct,
        Action<Func<Task>>? registerPostAcknowledgement = null)
    {
        if (mode == InboundProcessingMode.Background
            && OnlineReplicationWakeInboundPolicy.RequiresForeground(envelope.Kind))
            return InboundDisposition.Defer;

        try
        {
            await HandleInboundAsync(
                envelope,
                mode,
                identity,
                sessionSupportsReplication,
                ct,
                registerPostAcknowledgement);
            return InboundDisposition.Processed;
        }
        catch (InboundRetryException ex)
        {
            TraceTransport("receive-retry", ex.Message);
            return InboundDisposition.Retry;
        }
        catch (InboundPermanentRejectException ex)
        {
            var reason = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (reason.Length > 200) reason = reason[..200];
            var rejectionId = "envelope:" + StableEnvelopeId(
                "inbound.reject",
                $"{AppState.Norm(envelope.From)}\0{envelope.FromDevice}\0{envelope.Id}\0{envelope.Kind}");
            if (!state.SaveInboundRejection(new MeshDb.InboundRejectionItem(
                    rejectionId,
                    envelope.Id,
                    null,
                    envelope.Kind,
                    AppState.Norm(envelope.From),
                    envelope.FromDevice,
                    reason,
                    DateTimeOffset.UtcNow)))
            {
                TraceTransport("receive-rejection-persistence-failed", reason);
                return InboundDisposition.Retry;
            }
            TraceTransport("receive-permanent-reject", reason);
            return InboundDisposition.PermanentReject;
        }
    }

    public async Task<OnlineReplicationWakeResult> SynchronizePendingAsync(CancellationToken ct = default)
    {
        ResumeTransport();
        if (!Connected)
            await ConnectAsync().ConfigureAwait(false);
        var engine = OnlineReplicationEngine;
        var poller = replicationPoller;
        if (engine is null || poller is null)
            return OnlineReplicationWakeResult.NoData();
        await poller.PollOnceAsync(ct).ConfigureAwait(false);
        return OnlineReplicationWakeResult.NoData();
    }

}
