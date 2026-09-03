using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.LiveFaults;

public static class LiveFaultAdminEndpoints
{
    public static void MapLiveFaultAdminEndpoints(
        this WebApplication app,
        LiveFaultStore store,
        string? adminKey,
        IRelayStore? relayStore = null,
        LiveFaultTransportObserver? transportObserver = null,
        LiveFaultHandshakeObserver? handshakeObserver = null,
        LiveFaultAuthorityObserver? authorityObserver = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Enabled) return;

        bool Authorized(HttpContext context)
            => LiveFaultAdminAuthorization.IsAuthorized(
                adminKey,
                context.Request.Headers["X-Mesh-Admin-Key"].FirstOrDefault());

        app.MapPost("/admin/live-faults", (HttpContext context, LiveFaultActivationRequest request) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            try
            {
                return Results.Ok(store.Activate(request));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapGet("/admin/live-faults", (HttpContext context) =>
            !Authorized(context) ? Results.Unauthorized() : Results.Ok(store.List()));
        app.MapGet("/admin/live-faults/audit", (HttpContext context) =>
            !Authorized(context) ? Results.Unauthorized() : Results.Ok(store.Audit()));
        app.MapGet("/admin/live-faults/{ruleId}", (HttpContext context, string ruleId) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            var status = store.Get(ruleId);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });
        app.MapDelete("/admin/live-faults/{ruleId}", (HttpContext context, string ruleId) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            var changed = store.Deactivate(ruleId);
            return Results.Ok(new { ruleId, active = false, changed });
        });
        app.MapPost("/admin/live-faults/cleanup", (HttpContext context) =>
            !Authorized(context)
                ? Results.Unauthorized()
                : Results.Ok(new { expired = store.CleanupExpired() }));
        app.MapGet("/admin/live-faults/runtime", (HttpContext context) =>
            !Authorized(context)
                ? Results.Unauthorized()
                : Results.Ok(new
                {
                    attempts = transportObserver?.Snapshot()
                               ?? Array.Empty<LiveFaultTransportAttempt>(),
                    handshakes = handshakeObserver?.Events
                                 ?? Array.Empty<LiveFaultHandshakeEvent>(),
                    authorityLookups = authorityObserver?.Snapshot()
                                      ?? Array.Empty<LiveFaultAuthorityLookup>()
                }));
        app.MapPost("/admin/live-faults/rotate-authority", async (
            HttpContext context,
            LiveFaultAuthorityRotationRequest request) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            if (relayStore is null) return Results.NotFound();
            var handle = request.Handle.Trim().ToLowerInvariant();
            if (handle.Length == 0
                || string.IsNullOrWhiteSpace(request.PreviousDeviceId)
                || string.IsNullOrWhiteSpace(request.NewDevicePublicKey)
                || string.IsNullOrWhiteSpace(request.NewCustodyHead))
                return Results.BadRequest(new { error = "complete rotation authority is required" });

            var current = await relayStore.GetHandleAsync(handle, context.RequestAborted);
            if (current is null) return Results.NotFound();
            if (!current.DevicePublicKeys.Any(key =>
                    string.Equals(
                        DeviceProtocol.DeviceId(key),
                        request.PreviousDeviceId,
                        StringComparison.Ordinal)))
                return Results.BadRequest(new { error = "previous device is not authoritative" });

            var linked = await relayStore.UpsertHandleAsync(
                handle,
                request.NewDevicePublicKey,
                current.DisplayName,
                allowNewDevice: true,
                ct: context.RequestAborted);
            if (!linked.deviceAuthorized)
                return Results.BadRequest(new { error = "new device could not be linked" });
            var revoked = await relayStore.RevokeDeviceAsync(
                handle,
                request.PreviousDeviceId,
                request.NewDevicePublicKey,
                context.RequestAborted);
            if (!revoked.Revoked)
                return Results.BadRequest(new { error = "previous authority could not be revoked" });
            var advanced = await relayStore.AdvanceCustodyAsync(
                handle,
                revoked.AuthGeneration,
                revoked.AuthGeneration,
                request.NewCustodyHead,
                context.RequestAborted);
            if (!advanced)
                return Results.BadRequest(new { error = "custody authority could not be advanced" });

            var rotated = await relayStore.GetHandleAsync(handle, context.RequestAborted);
            return Results.Ok(new LiveFaultAuthorityRotationResult(
                handle,
                request.PreviousDeviceId,
                DeviceProtocol.DeviceId(request.NewDevicePublicKey),
                rotated!.AuthGeneration,
                rotated.CustodyHead,
                LiveFaultAuthorityObserver.Fingerprint(request.NewDevicePublicKey)));
        });
    }
}
