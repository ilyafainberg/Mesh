using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Mesh 1.17 device-sync integration for <see cref="MeshClient"/>.
///
/// Desktop-only asset upsert/delete and cross-platform ask-user prompt/resolution operations ride
/// inside the existing encrypted, queued, ACKed Protocol 8 device-sync transport as a dedicated
/// <see cref="Mesh117SyncKinds.Envelope117Operation"/> envelope. Nothing here introduces a new
/// protocol version or bypasses encryption: outbound uses the same
/// <c>SendDeviceSyncEnvelopeWithOutcomeAsync</c> path (encrypt, sign, relay QueueEnqueue) and inbound
/// persists before the caller ACKs, exactly like every other device-sync kind.
///
/// This partial binds to the sibling <see cref="AppState"/> hooks that exist today:
/// <c>AssetMutationCreated</c> (nudge the durable asset outbox), <c>AskUserPromptCreated</c> and
/// <c>AskUserPromptResolved</c> (fan out prompt/resolution), and the inbound
/// <c>ReceiveRemoteAskUserPromptAsync</c>/<c>ReceiveRemoteAskUserResolutionAsync</c> seams.
/// </summary>
public sealed partial class MeshClient
{
    // Serialises asset fan-out so a burst of local mutations does not launch overlapping drains.
    private readonly SemaphoreSlim asset117SendGate = new(1, 1);

    // Serialises ask-user fan-out independently of asset drains.
    private readonly SemaphoreSlim askUser117SendGate = new(1, 1);

    /// <summary>Wires the local-origin 1.17 events raised by <see cref="AppState"/> to fan-out. Called
    /// once from the constructor.</summary>
    private void InitializeMesh117Sync()
    {
        state.AssetMutationCreated += OnAssetMutationCreated;
        state.AskUserPromptCreated += OnAskUserPromptCreated;
        state.AskUserPromptResolved += OnAskUserPromptResolved;
    }

    // ----------------------------------------------------------------------
    // Local-origin fan-out (event handlers).
    // ----------------------------------------------------------------------

    private void OnAssetMutationCreated(AppState.AssetSyncMutation _)
    {
        // The durable asset outbox is the source of truth; the event is only a low-latency nudge.
        var identity = authenticatedDeviceSyncIdentity;
        if (identity is null || !Connected || !supportsSendResults || !supportsDeviceSync)
            return;
        TrackBackground(DrainAsset117OutboxAsync(identity), "asset 1.17 outbox fan-out");
    }

    private void OnAskUserPromptCreated(AskUserPrompt prompt)
    {
        var identity = authenticatedDeviceSyncIdentity;
        if (identity is null || !Connected || !supportsSendResults || !supportsDeviceSync)
            return;
        // Only the origin device fans a prompt out. A prompt received from elsewhere also raises this
        // event (the sibling re-surfaces it); gating on origin prevents an infinite re-broadcast loop.
        if (!string.Equals(prompt.OriginDeviceId, identity.DeviceId, StringComparison.Ordinal))
            return;
        var payload = AppState.BuildPromptPayload(prompt);
        var operation = Mesh117Operations.BuildPromptOperation(payload);
        TrackBackground(
            BroadcastAskUser117OperationAsync(identity, operation),
            "ask-user prompt 1.17 fan-out");
    }

    private void OnAskUserPromptResolved(AskUserPrompt prompt)
    {
        var identity = authenticatedDeviceSyncIdentity;
        if (identity is null || !Connected || !supportsSendResults || !supportsDeviceSync)
            return;
        // Only the winning resolver fans the resolution out. A resolution applied from a remote device
        // also raises this event; gating on the resolver id prevents a re-broadcast ping-pong.
        if (!string.Equals(prompt.ResolutionDeviceId, identity.DeviceId, StringComparison.Ordinal))
            return;
        var payload = AppState.BuildResolutionPayload(prompt);
        var operation = Mesh117Operations.BuildResolutionOperation(payload, identity.DeviceId);
        TrackBackground(
            BroadcastAskUser117OperationAsync(identity, operation),
            "ask-user resolution 1.17 fan-out");
    }

    /// <summary>Drains the durable asset outbox on reconnect/startup after the device-sync handshake.</summary>
    private void Drain117OnConnect(DeviceSyncIdentity identity)
        => TrackBackground(Recover117OnConnectAsync(identity), "1.17 reconnect recovery");

    private async Task Recover117OnConnectAsync(DeviceSyncIdentity identity)
    {
        await DrainAsset117OutboxAsync(identity);
        await SendAsset117SnapshotAsync(identity);

        var pending = await state.ListAllPendingAskUserPromptsAsync();
        foreach (var prompt in pending)
        {
            if (!IsCurrentDeviceSyncIdentity(identity)) return;
            if (!string.Equals(prompt.OriginDeviceId, identity.DeviceId, StringComparison.Ordinal))
                continue;
            var operation = Mesh117Operations.BuildPromptOperation(AppState.BuildPromptPayload(prompt));
            await BroadcastAskUser117OperationAsync(identity, operation);
        }

        var resolutions = await state.ListResolvedAskUserPromptsAsync(identity.DeviceId);
        foreach (var resolution in resolutions)
        {
            if (!IsCurrentDeviceSyncIdentity(identity)) return;
            var operation = Mesh117Operations.BuildResolutionOperation(
                AppState.BuildResolutionPayload(resolution), identity.DeviceId);
            await BroadcastAskUser117OperationAsync(identity, operation);
        }
    }

    // ----------------------------------------------------------------------
    // Outbound: asset outbox drain (desktop-to-desktop only).
    // ----------------------------------------------------------------------

    private async Task DrainAsset117OutboxAsync(DeviceSyncIdentity identity)
    {
        await asset117SendGate.WaitAsync();
        try
        {
            if (!IsCurrentDeviceSyncIdentity(identity) || !supportsSendResults || !supportsDeviceSync)
                return;

            // Authoritative platforms from the linked-device directory. Never inferred from names.
            var devices = await ListMyDevicesAsync();
            if (!IsCurrentDeviceSyncIdentity(identity))
                return;
            var localPlatform = FindPlatform(devices, identity.DeviceId);
            var otherDevices = devices
                .Where(device => !string.Equals(device.DeviceId, identity.DeviceId, StringComparison.Ordinal))
                .ToList();
            var desktopTargets = Mesh117Routing.EligibleAssetTargets(localPlatform, otherDevices);

            var processed = new HashSet<string>(StringComparer.Ordinal);
            while (IsCurrentDeviceSyncIdentity(identity))
            {
                var batch = await state.DequeueAsset117OutboxAsync(100);
                var fresh = batch.Where(item => !processed.Contains(item.OperationId)).ToList();
                if (fresh.Count == 0)
                    return;
                foreach (var item in fresh)
                {
                    if (!IsCurrentDeviceSyncIdentity(identity))
                        return;
                    processed.Add(item.OperationId);
                    await DispatchAsset117OutboxItemAsync(identity, item, localPlatform, desktopTargets);
                }
                if (processed.Count >= 100_000)
                    return;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"asset 1.17 outbox drain failed: {ex.Message}");
        }
        finally
        {
            asset117SendGate.Release();
        }
    }

    private async Task DispatchAsset117OutboxItemAsync(
        DeviceSyncIdentity identity,
        AssetOutboxItem item,
        string? localPlatform,
        IReadOnlyList<string> desktopTargets)
    {
        // A non-desktop origin must never emit an asset operation. Reject permanently so we do not spin.
        if (!DevicePlatforms.IsDesktop(localPlatform))
        {
            await state.MarkAsset117OutboxAsync(item.OperationId, success: false, "source_not_desktop");
            return;
        }

        var full = await state.GetAsset117FullAsync(item.Kind, item.AssetId);
        if (full is not { } asset)
        {
            // The row is gone (hard-deleted); there is nothing to deliver. Clear the entry.
            await state.MarkAsset117OutboxAsync(item.OperationId, success: true, null);
            return;
        }

        string body;
        string operationId;
        try
        {
            var operation = Mesh117Operations.BuildAssetOperation(asset.Summary, asset.Content);
            operationId = operation.OperationId;
            if (!asset.Summary.IsDeleted)
                Mesh117SnapshotPlanner.EnsureAssetContentFits(asset.Summary.Id, asset.Content.LongLength);
            var deviceBatch = new DeviceSyncBatch(
                DeviceSyncEnvelopeIdProtocol.LiveBatchId(identity.DeviceId, new[] { operation }),
                identity.DeviceId,
                IsSnapshot: false,
                new[] { operation });
            body = JsonSerializer.Serialize(deviceBatch, Json);
            Mesh117SnapshotPlanner.EnsureOperationFits(operationId, Encoding.UTF8.GetByteCount(body));
        }
        catch (Mesh117PayloadTooLargeException ex)
        {
            await state.DeadLetterAsset117OutboxAsync(item.OperationId, ex.Message);
            Log?.Invoke($"asset 1.17 operation dead-lettered: {ex.Message}");
            return;
        }

        if (desktopTargets.Count == 0)
        {
            // No eligible desktop targets right now. Newly linked/reconnected desktops are backfilled by
            // the snapshot channel, so consume the entry rather than spin on it.
            await state.MarkAsset117OutboxAsync(item.OperationId, success: true, null);
            return;
        }

        var allAccepted = true;
        string? lastError = null;
        foreach (var target in desktopTargets)
        {
            if (!IsCurrentDeviceSyncIdentity(identity))
            {
                allAccepted = false;
                lastError = "identity_changed";
                break;
            }
            var outcome = await SendDeviceSyncEnvelopeWithOutcomeAsync(
                identity, target, Mesh117SyncKinds.Envelope117Operation, body, CancellationToken.None);
            if (outcome == DeviceSyncSendOutcome.TooLarge)
            {
                await state.DeadLetterAsset117OutboxAsync(
                    item.OperationId, "The encrypted asset envelope exceeded the transport limit.");
                Log?.Invoke($"asset 1.17 operation {item.OperationId} dead-lettered: envelope too large");
                return;
            }
            if (outcome != DeviceSyncSendOutcome.Accepted)
            {
                allAccepted = false;
                lastError = "deferred";
            }
        }

        // Success only when every intended desktop target accepted; otherwise retain for retry.
        await state.MarkAsset117OutboxAsync(
            item.OperationId, allAccepted, allAccepted ? null : lastError ?? "deferred");
    }

    /// <summary>
    /// Backfills the complete asset set to linked desktop devices in bounded pages. Each asset uses
    /// one envelope so a large item cannot force the whole page over the transport limit.
    /// </summary>
    private async Task SendAsset117SnapshotAsync(DeviceSyncIdentity identity)
    {
        await asset117SendGate.WaitAsync();
        try
        {
            if (!IsCurrentDeviceSyncIdentity(identity) || !supportsSendResults || !supportsDeviceSync)
                return;

            var devices = await ListMyDevicesAsync();
            var localPlatform = FindPlatform(devices, identity.DeviceId);
            var targets = Mesh117Routing.EligibleAssetTargets(
                localPlatform,
                devices.Where(device =>
                    !string.Equals(device.DeviceId, identity.DeviceId, StringComparison.Ordinal)));
            if (targets.Count == 0) return;

            foreach (var kind in Enum.GetValues<AssetKind>())
            {
                string? afterId = null;
                while (IsCurrentDeviceSyncIdentity(identity))
                {
                    var page = await state.PageAsset117SummariesAsync(
                        kind, Mesh117SnapshotPlanner.MaxPageSize, afterId);
                    if (page.Count == 0) break;

                    foreach (var summary in page)
                    {
                        afterId = summary.Id;
                        if (summary.LocalOnly) continue;
                        var full = await state.GetAsset117FullAsync(kind, summary.Id);
                        if (full is not { } asset) continue;

                        DeviceSyncOperation operation;
                        string body;
                        try
                        {
                            if (!asset.Summary.IsDeleted)
                            {
                                Mesh117SnapshotPlanner.EnsureAssetContentFits(
                                    asset.Summary.Id, asset.Content.LongLength);
                            }
                            operation = Mesh117Operations.BuildAssetOperation(
                                asset.Summary, asset.Content, identity.DeviceId);
                            var batch = new DeviceSyncBatch(
                                DeviceSyncEnvelopeIdProtocol.LiveBatchId(
                                    identity.DeviceId, new[] { operation }),
                                identity.DeviceId,
                                IsSnapshot: true,
                                new[] { operation });
                            body = JsonSerializer.Serialize(batch, Json);
                            Mesh117SnapshotPlanner.EnsureOperationFits(
                                operation.OperationId, Encoding.UTF8.GetByteCount(body));
                        }
                        catch (Mesh117PayloadTooLargeException ex)
                        {
                            Log?.Invoke($"asset 1.17 snapshot skipped {summary.Id}: {ex.Message}");
                            continue;
                        }

                        foreach (var target in targets)
                        {
                            if (!IsCurrentDeviceSyncIdentity(identity)) return;
                            var outcome = await SendDeviceSyncEnvelopeWithOutcomeAsync(
                                identity,
                                target,
                                Mesh117SyncKinds.Envelope117Operation,
                                body,
                                CancellationToken.None);
                            if (outcome != DeviceSyncSendOutcome.Accepted)
                                Log?.Invoke(
                                    $"asset 1.17 snapshot delivery to {target} was {outcome}");
                        }
                    }

                    if (page.Count < Mesh117SnapshotPlanner.MaxPageSize) break;
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"asset 1.17 snapshot backfill failed: {ex.Message}");
        }
        finally
        {
            asset117SendGate.Release();
        }
    }

    // ----------------------------------------------------------------------
    // Outbound: ask-user prompt/resolution fan-out to all eligible devices.
    // ----------------------------------------------------------------------

    private async Task BroadcastAskUser117OperationAsync(
        DeviceSyncIdentity identity, DeviceSyncOperation operation)
    {
        await askUser117SendGate.WaitAsync();
        try
        {
            if (!IsCurrentDeviceSyncIdentity(identity) || !supportsSendResults || !supportsDeviceSync)
                return;

            // Ask-user reaches every same-account device (mobile included). Relay QueueEnqueue triggers
            // the mobile push-registration path for offline targets; the deep-link travels in the payload.
            var targets = await GetDeviceSyncTargetsAsync(identity, refresh: false);
            if (targets.Count == 0)
                return;

            var deviceBatch = new DeviceSyncBatch(
                DeviceSyncEnvelopeIdProtocol.LiveBatchId(identity.DeviceId, new[] { operation }),
                identity.DeviceId,
                IsSnapshot: false,
                new[] { operation });
            var body = JsonSerializer.Serialize(deviceBatch, Json);

            foreach (var target in targets)
            {
                if (!IsCurrentDeviceSyncIdentity(identity))
                    return;
                var outcome = await SendDeviceSyncEnvelopeWithOutcomeAsync(
                    identity, target, Mesh117SyncKinds.Envelope117Operation, body, CancellationToken.None);
                if (outcome == DeviceSyncSendOutcome.TooLarge)
                    Log?.Invoke($"ask-user 1.17 fan-out to {target} exceeded the envelope limit");
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"ask-user 1.17 fan-out failed: {ex.Message}");
        }
        finally
        {
            askUser117SendGate.Release();
        }
    }

    // ----------------------------------------------------------------------
    // Inbound apply. Persist before ACK; validation failures reject permanently;
    // transient store unavailability retries. Mirrors Protocol 8 semantics.
    // ----------------------------------------------------------------------

    private async Task ApplyInbound117OperationAsync(
        MeshEnvelope env, string plaintext, string myDeviceId, CancellationToken ct)
    {
        var batch = JsonSerializer.Deserialize<DeviceSyncBatch>(plaintext, Json)
            ?? throw new JsonException("The 1.17 operation batch was null.");
        ValidateDeviceSyncBatch(batch, env.FromDevice);
        if (!state.Mesh117PersistenceAvailable)
            throw new InboundRetryException("mesh117_persistence_unavailable");

        // Asset routes must be validated against authoritative platforms BEFORE any DB mutation. Both the
        // source and this local device must be desktop; anything else is a permanent, pre-mutation reject.
        if (batch.Operations.Any(operation => Mesh117SyncKinds.IsAssetKind(operation.Kind)))
        {
            var devices = await ListMyDevicesAsync(ct);
            var localPlatform = FindPlatform(devices, myDeviceId);
            var sourcePlatform = FindPlatform(devices, env.FromDevice);
            if (localPlatform is null || sourcePlatform is null)
                throw new InboundRetryException("mesh117_asset_directory_incomplete");
            if (!Mesh117Routing.CanReceiveAsset(localPlatform, sourcePlatform))
                throw new InboundPermanentRejectException("mesh117_asset_route_invalid");
        }

        foreach (var operation in batch.Operations)
            await ApplyInbound117OperationCoreAsync(operation, ct);
    }

    private async Task ApplyInbound117OperationCoreAsync(DeviceSyncOperation operation, CancellationToken ct)
    {
        try
        {
            switch (operation.Kind)
            {
                case Mesh117SyncKinds.AssetUpsert:
                case Mesh117SyncKinds.AssetDelete:
                {
                    var payload = JsonSerializer.Deserialize<Asset117Payload>(operation.Payload, Json)
                        ?? throw new JsonException("The asset payload was null.");
                    var outcome = await state.ApplyRemoteAsset117Async(payload, ct);
                    if (outcome == Mesh117ApplyOutcome.Unavailable)
                        throw new InboundRetryException("mesh117_apply_unavailable");
                    return;
                }
                case Mesh117SyncKinds.AskUserPrompt:
                {
                    var payload = JsonSerializer.Deserialize<AskUser117PromptPayload>(operation.Payload, Json)
                        ?? throw new JsonException("The ask-user prompt payload was null.");
                    Mesh117PayloadGuard.ValidatePrompt(payload);
                    var prompt = Mesh117Operations.PayloadToAskUserPrompt(payload);
                    // Idempotent insert; the sibling raises the AppState/UI change on a genuinely new prompt.
                    await state.ReceiveRemoteAskUserPromptAsync(prompt, ct);
                    return;
                }
                case Mesh117SyncKinds.AskUserResolution:
                {
                    var payload = JsonSerializer.Deserialize<AskUser117ResolutionPayload>(operation.Payload, Json)
                        ?? throw new JsonException("The ask-user resolution payload was null.");
                    Mesh117PayloadGuard.ValidateResolution(payload);
                    var existing = await state.LoadAskUserPromptAsync(payload.PromptId, ct);
                    if (existing is null)
                    {
                        if (payload.Prompt is null)
                            throw new InboundPermanentRejectException(
                                "mesh117_ask_resolution_prompt_missing");
                        var prompt = Mesh117Operations.PayloadToAskUserPrompt(payload.Prompt);
                        await state.ReceiveRemoteAskUserPromptAsync(prompt, ct);
                    }
                    // Atomic first-writer-wins in the store; every device converges on the same winner.
                    await state.ReceiveRemoteAskUserResolutionAsync(
                        payload.PromptId, payload.Selection, payload.ResolutionDeviceId,
                        payload.IdempotencyToken, ct);
                    return;
                }
                default:
                    throw new InvalidDataException($"Unknown 1.17 operation kind '{operation.Kind}'.");
            }
        }
        catch (Mesh117PayloadInvalidException ex)
        {
            // Bad data can never become good: reject permanently instead of retrying.
            throw new InboundPermanentRejectException("mesh117_payload_invalid", ex);
        }
    }

    private static string? FindPlatform(IReadOnlyList<Mesh.Shared.DeviceInfo> devices, string deviceId)
    {
        foreach (var device in devices)
        {
            if (string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal))
                return device.Platform;
        }
        return null;
    }
}
