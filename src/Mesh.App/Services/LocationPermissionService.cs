namespace Mesh.App.Services;

public enum LocationPermissionState
{
    Granted,
    PermissionRequired,
    ServicesDisabled,
    Unavailable,
    Unsupported
}

public sealed record LocationPermissionStatus(LocationPermissionState State, string Message)
{
    public bool IsGranted => State == LocationPermissionState.Granted;
}

public sealed class LocationPermissionService
{
#if WINDOWS
    private Windows.Devices.Geolocation.GeolocationAccessStatus? lastWindowsAccessStatus;
#endif

    public async Task<LocationPermissionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

#if ANDROID || IOS
        try
        {
            var permission = await Microsoft.Maui.ApplicationModel.Permissions
                .CheckStatusAsync<Microsoft.Maui.ApplicationModel.Permissions.LocationWhenInUse>()
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (permission != Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
                return PermissionRequired();
#if ANDROID
            if (!HasAndroidPreciseLocation())
                return PermissionRequired();
#endif

            return Microsoft.Maui.Devices.Sensors.Geolocation.Default.IsEnabled
                ? Granted()
                : ServicesDisabled();
        }
        catch (Microsoft.Maui.ApplicationModel.FeatureNotSupportedException)
        {
            return Unsupported();
        }
        catch (Microsoft.Maui.ApplicationModel.PermissionException)
        {
            return PermissionRequired();
        }
#elif WINDOWS
        if (lastWindowsAccessStatus is null
            || lastWindowsAccessStatus == Windows.Devices.Geolocation.GeolocationAccessStatus.Denied)
            return PermissionRequired();
        if (lastWindowsAccessStatus == Windows.Devices.Geolocation.GeolocationAccessStatus.Unspecified)
            return Unavailable("Windows could not determine location permission. Check Privacy and security > Location.");

        try
        {
            var locationStatus = new Windows.Devices.Geolocation.Geolocator().LocationStatus;
            return locationStatus switch
            {
                Windows.Devices.Geolocation.PositionStatus.Disabled
                    when lastWindowsAccessStatus == Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed =>
                    ServicesDisabled(),
                Windows.Devices.Geolocation.PositionStatus.Disabled => PermissionRequired(),
                Windows.Devices.Geolocation.PositionStatus.NotAvailable => Unavailable(
                    "Windows location is unavailable on this device."),
                _ => Granted()
            };
        }
        catch (UnauthorizedAccessException)
        {
            return PermissionRequired();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Unavailable("Windows location is unavailable on this device.");
        }
#else
        await Task.CompletedTask;
        return Unsupported();
#endif
    }

    public async Task<LocationPermissionStatus> RequestAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

#if ANDROID || IOS
        try
        {
            var permission = await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(
                () => Microsoft.Maui.ApplicationModel.Permissions
                    .RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.LocationWhenInUse>());
            ct.ThrowIfCancellationRequested();

            if (permission != Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
                return PermissionRequired();
#if ANDROID
            if (!HasAndroidPreciseLocation())
                return PermissionRequired();
#endif

            return Microsoft.Maui.Devices.Sensors.Geolocation.Default.IsEnabled
                ? Granted()
                : ServicesDisabled();
        }
        catch (Microsoft.Maui.ApplicationModel.FeatureNotSupportedException)
        {
            return Unsupported();
        }
        catch (Microsoft.Maui.ApplicationModel.PermissionException)
        {
            return PermissionRequired();
        }
#elif WINDOWS
        try
        {
            lastWindowsAccessStatus = await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(
                async () => await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync());
            ct.ThrowIfCancellationRequested();

            return lastWindowsAccessStatus switch
            {
                Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed =>
                    await GetStatusAsync(ct).ConfigureAwait(false),
                Windows.Devices.Geolocation.GeolocationAccessStatus.Denied => PermissionRequired(),
                _ => Unavailable("Windows could not determine location permission. Check Privacy and security > Location.")
            };
        }
        catch (UnauthorizedAccessException)
        {
            lastWindowsAccessStatus = Windows.Devices.Geolocation.GeolocationAccessStatus.Denied;
            return PermissionRequired();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Unavailable("Windows could not access the native location service.");
        }
#else
        await Task.CompletedTask;
        return Unsupported();
#endif
    }

#if ANDROID
    private static bool HasAndroidPreciseLocation()
        => Android.App.Application.Context.CheckSelfPermission(
            Android.Manifest.Permission.AccessFineLocation) == Android.Content.PM.Permission.Granted;
#endif

    private static LocationPermissionStatus Granted() =>
        new(LocationPermissionState.Granted, "Precise device location is available.");

    private static LocationPermissionStatus PermissionRequired() =>
        new(LocationPermissionState.PermissionRequired,
            "Permission required. Allow precise location in the operating system settings.");

    private static LocationPermissionStatus ServicesDisabled() =>
        new(LocationPermissionState.ServicesDisabled,
            "Location services are off. Turn them on in the operating system settings.");

    private static LocationPermissionStatus Unavailable(string message) =>
        new(LocationPermissionState.Unavailable, message);

    private static LocationPermissionStatus Unsupported() =>
        new(LocationPermissionState.Unsupported,
            "Native location is not supported on this platform.");
}
