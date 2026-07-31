using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Pure platform policy for asset synchronisation.
///
/// Asset operations (Skill / Knowledge / Widget content) are desktop-to-desktop only.
/// Mobile devices hold their assets locally and never participate in asset sync as
/// either a source or a target. Non-asset operations are not restricted.
/// </summary>
public static class AssetSyncPolicy
{
    /// <summary>
    /// Returns true when the combination of source and target platforms allows the
    /// requested operation type.
    ///
    /// Rules:
    ///   - Non-asset operations: always allowed regardless of platform.
    ///   - Asset operations: both source and target must be desktop platforms
    ///     (Windows or macOS). Unknown, Android, iOS or null are all rejected.
    /// </summary>
    public static bool IsAllowed(
        string? sourcePlatform,
        string? targetPlatform,
        bool isAssetOperation)
    {
        if (!isAssetOperation)
            return true;

        return DevicePlatforms.IsDesktop(sourcePlatform)
            && DevicePlatforms.IsDesktop(targetPlatform);
    }
}
