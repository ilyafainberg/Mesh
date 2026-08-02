using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>The outcome of evaluating whether a local tool may be offered to an agent turn.</summary>
public sealed class ToolAvailabilityResult
{
    public bool Allowed { get; init; }

    /// <summary>Human-readable denial reason. Non-null only when <see cref="Allowed"/> is false.</summary>
    public string? DenialReason { get; init; }

    /// <summary>Returns an allowed result.</summary>
    public static ToolAvailabilityResult Allow() => new() { Allowed = true };

    /// <summary>Returns a denied result with a mandatory reason.</summary>
    public static ToolAvailabilityResult Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Allowed = false, DenialReason = reason };
    }
}

/// <summary>
/// Pure, injectable policy that decides whether a <see cref="LocalToolKind"/> may be exposed to
/// an agent turn. This is the single authoritative place for the following rules:
/// <list type="number">
///   <item>The tool must be enabled in the stored <see cref="LocalToolSetting"/> (absent = disabled).</item>
///   <item>Desktop-only tools are unconditionally denied when the host device is mobile (Android /
///         iOS), even when the profile has the tool marked enabled from a prior desktop sync
///         (fail-closed against profile propagation artifacts).</item>
///   <item><see cref="LocalToolKind.MeshData"/> is always owner-only; it is never shared with any
///         guest, regardless of the stored visibility value.</item>
///   <item>For a guest turn, the tool's visibility must grant access from the caller's circles:
///         either <c>AllAllowedContacts</c> ("public"), or a <c>SelectedCircles</c> set that
///         intersects the caller's circle list. Private (the default) is always denied.</item>
/// </list>
/// All rules are applied in order; the first failing check produces the denial reason. A result
/// with <see cref="ToolAvailabilityResult.Allowed"/> == false must never be offered to the agent.
/// </summary>
public static class CircleToolPolicy
{
    /// <summary>
    /// Evaluate whether <paramref name="kind"/> may be included in the tool set for an agent turn.
    /// </summary>
    /// <param name="kind">The tool kind to evaluate.</param>
    /// <param name="setting">
    /// The stored profile row for this tool. A null setting is treated as disabled (the
    /// absent-means-disabled invariant from <see cref="MeshProfile.LocalTools"/>).
    /// </param>
    /// <param name="isOwner">
    /// True when the agent turn is running for the device owner (private chat or own-thread).
    /// False for any guest / contact-initiated turn.
    /// </param>
    /// <param name="guestCircles">
    /// The circles the requesting contact belongs to (taken from the owner's contact row).
    /// Only consulted when <paramref name="isOwner"/> is false. Null is treated as empty.
    /// </param>
    /// <param name="isMobile">
    /// True when the host device is Android or iOS. Pass <see cref="PlatformCaps.IsMobile"/>
    /// in production.
    /// </param>
    public static ToolAvailabilityResult Evaluate(
        LocalToolKind kind,
        LocalToolSetting? setting,
        bool isOwner,
        IEnumerable<string>? guestCircles,
        bool isMobile)
    {
        // Rule 1 – absent / disabled (absent-means-disabled invariant).
        if (setting is null || !setting.Enabled)
            return ToolAvailabilityResult.Deny($"{kind} is not enabled in the profile.");

        // Rule 2 – mobile hard-blocks desktop-only tools, even when the stored profile has the
        // tool enabled (e.g. a desktop profile synced to a phone must still fail closed here).
        if (isMobile && kind.IsDesktopOnly())
            return ToolAvailabilityResult.Deny(
                $"{kind} requires a desktop environment and cannot run on a mobile device.");

        // Rule 3 – MeshData is a hard privacy boundary; the guest agent must never see it.
        if (!isOwner && kind == LocalToolKind.MeshData)
            return ToolAvailabilityResult.Deny(
                $"{kind} is owner-only and may never be exposed to a guest agent.");

        // Rule 4 – guest audience check: deny-by-default; only explicit grants allow.
        if (!isOwner)
        {
            var circles = guestCircles ?? Enumerable.Empty<string>();
            if (!AudiencePolicy.CanAccess(setting.Visibility, circles))
                return ToolAvailabilityResult.Deny(
                    $"{kind} is not shared with the caller's circles " +
                    $"(stored visibility: '{setting.Visibility ?? CapabilityAudience.PrivateVisibility}').");
        }

        return ToolAvailabilityResult.Allow();
    }
}
