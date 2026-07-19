using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Owner-gated tool that reports precise device location through the native operating system service.
/// </summary>
public sealed class GeoLocationTool(LocationPermissionService permissionService) : IAgentTool
{
    private const double MaximumAcceptedAccuracyMeters = 500;

    public string Name => "geolocation";

    public string Description =>
        "Get this device's current precise latitude and longitude using the native operating system " +
        "location service. Native location permission must already be granted.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            accuracy = new
            {
                type = "string",
                description = "Optional: 'medium' (default) or 'high'. Higher accuracy can take longer."
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var accuracy = ToolArgs.GetString(args, "accuracy").Trim().ToLowerInvariant();
        if (accuracy.Length > 0 && accuracy is not "medium" and not "high")
            return "ERROR: accuracy must be 'medium' or 'high' for precise device location.";

        LocationPermissionStatus permission;
        try
        {
            permission = await permissionService.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "ERROR: location request was canceled.";
        }

        if (!permission.IsGranted)
            return $"ERROR: {permission.Message}";

        try
        {
            var accuracyLevel = accuracy == "high"
                ? Microsoft.Maui.Devices.Sensors.GeolocationAccuracy.Best
                : Microsoft.Maui.Devices.Sensors.GeolocationAccuracy.Medium;
            var request = new Microsoft.Maui.Devices.Sensors.GeolocationRequest(
                accuracyLevel, TimeSpan.FromSeconds(20));
#if IOS
            request.RequestFullAccuracy = true;
#endif
            var location = await Microsoft.Maui.Devices.Sensors.Geolocation.Default
                .GetLocationAsync(request, ct).ConfigureAwait(false);

            if (location is null)
                return "ERROR: precise device location is unavailable. Check location services and try again.";
            if (location.Accuracy is not > 0)
                return "ERROR: the operating system returned a location without accuracy information. Try again outdoors.";
            if (location.Accuracy > MaximumAcceptedAccuracyMeters)
                return $"ERROR: only a coarse location was available (about {location.Accuracy:F0} m accuracy). " +
                    "Move to an area with a better GPS signal and try again.";

            return $"OK precise device location: {location.Latitude:F6}, {location.Longitude:F6} " +
                $"(accurate to about {location.Accuracy:F0} m).";
        }
        catch (Microsoft.Maui.ApplicationModel.PermissionException)
        {
            return "ERROR: Permission required. Allow precise location in the operating system settings.";
        }
        catch (Microsoft.Maui.ApplicationModel.FeatureNotEnabledException)
        {
            return "ERROR: location services are off. Turn them on in the operating system settings.";
        }
        catch (Microsoft.Maui.ApplicationModel.FeatureNotSupportedException)
        {
            return "ERROR: native location is not supported on this platform.";
        }
        catch (TimeoutException)
        {
            return "ERROR: precise device location timed out. Check your GPS signal and try again.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "ERROR: location request was canceled.";
        }
        catch (OperationCanceledException)
        {
            return "ERROR: precise device location timed out. Check your GPS signal and try again.";
        }
    }
}
