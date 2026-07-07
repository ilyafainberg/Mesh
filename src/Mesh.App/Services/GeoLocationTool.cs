using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Owner-gated tool that reports the device's approximate geographic location. On Windows/mobile it
/// asks the OS geolocation service (via MAUI Geolocation), which prompts for permission the first
/// time. If that is unavailable or denied, it falls back to a coarse IP-based lookup so the agent can
/// still answer "where am I" style questions. Returns latitude/longitude plus, when available, a
/// human-readable place (city, region, country) via reverse geocoding.
/// </summary>
public sealed class GeoLocationTool : IAgentTool
{
    public string Name => "geolocation";

    public string Description =>
        "Get this device's current approximate location (latitude, longitude, and a place name when " +
        "available). Uses the operating system's location service, falling back to a coarse IP-based " +
        "lookup. Use for 'where am I', local time zone, or nearby-place questions.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            accuracy = new
            {
                type = "string",
                description = "Optional: 'low', 'medium' (default), or 'high'. Higher accuracy can take longer."
            }
        }
    };

    private readonly IHttpClientFactory? httpFactory;

    public GeoLocationTool(IHttpClientFactory? httpFactory = null) => this.httpFactory = httpFactory;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var accuracy = ToolArgs.GetString(args, "accuracy").Trim().ToLowerInvariant();

        // 1) Try the OS geolocation service (prompts for permission on first use).
        try
        {
            var osResult = await TryOsLocationAsync(accuracy, ct).ConfigureAwait(false);
            if (osResult is not null) return osResult;
        }
        catch (Exception ex)
        {
            // Permission denied, not supported, or timed out: fall through to the IP fallback.
            _ = ex;
        }

        // 2) Coarse IP-based fallback.
        try
        {
            var ipResult = await TryIpLocationAsync(ct).ConfigureAwait(false);
            if (ipResult is not null) return ipResult;
        }
        catch (Exception ex)
        {
            return $"ERROR: could not determine location: {ex.Message}";
        }

        return "ERROR: location is unavailable (the OS location service is off or denied, and the IP lookup failed).";
    }

    private static async Task<string?> TryOsLocationAsync(string accuracy, CancellationToken ct)
    {
        var accuracyLevel = accuracy switch
        {
            "low" => Microsoft.Maui.Devices.Sensors.GeolocationAccuracy.Low,
            "high" => Microsoft.Maui.Devices.Sensors.GeolocationAccuracy.Best,
            _ => Microsoft.Maui.Devices.Sensors.GeolocationAccuracy.Medium
        };

        var request = new Microsoft.Maui.Devices.Sensors.GeolocationRequest(accuracyLevel, TimeSpan.FromSeconds(20));
        var location = await Microsoft.Maui.Devices.Sensors.Geolocation.Default.GetLocationAsync(request, ct)
            .ConfigureAwait(false);
        if (location is null) return null;

        var place = await TryReverseGeocodeAsync(location.Latitude, location.Longitude).ConfigureAwait(false);
        var placePart = place is null ? "" : $" | Place: {place}";
        return $"OK location (device GPS/OS): {location.Latitude:F5}, {location.Longitude:F5}"
            + (location.Accuracy is > 0 ? $" (accurate to ~{location.Accuracy:F0} m)" : "")
            + placePart;
    }

    private static async Task<string?> TryReverseGeocodeAsync(double lat, double lon)
    {
        try
        {
            var placemarks = await Microsoft.Maui.Devices.Sensors.Geocoding.Default
                .GetPlacemarksAsync(lat, lon).ConfigureAwait(false);
            var p = placemarks?.FirstOrDefault();
            if (p is null) return null;
            var parts = new[] { p.Locality, p.AdminArea, p.CountryName }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var joined = string.Join(", ", parts);
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }
        catch { return null; }
    }

    private async Task<string?> TryIpLocationAsync(CancellationToken ct)
    {
        var http = httpFactory?.CreateClient("updater") ?? new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        // ipapi.co returns coarse city-level location for the caller's public IP, no key required.
        using var resp = await http.GetAsync("https://ipapi.co/json/", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        string? Get(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? GetNum(string k) => root.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : null;

        var lat = GetNum("latitude");
        var lon = GetNum("longitude");
        var city = Get("city");
        var region = Get("region");
        var country = Get("country_name");
        var place = string.Join(", ", new[] { city, region, country }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (lat is null || lon is null)
            return string.IsNullOrWhiteSpace(place) ? null : $"OK location (coarse, IP-based): {place}";

        var placePart = string.IsNullOrWhiteSpace(place) ? "" : $" | Place: {place}";
        return $"OK location (coarse, IP-based): {lat:F4}, {lon:F4}{placePart}. "
            + "Note: IP-based location is approximate (city-level) and may reflect your ISP or VPN.";
    }
}
