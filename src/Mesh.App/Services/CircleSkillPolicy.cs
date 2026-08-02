using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>The outcome of evaluating whether a skill may be offered to an agent turn.</summary>
public sealed class SkillAvailabilityResult
{
    public bool Allowed { get; init; }

    /// <summary>Human-readable denial reason. Non-null only when <see cref="Allowed"/> is false.</summary>
    public string? DenialReason { get; init; }

    /// <summary>
    /// Device-compatibility result when a compatibility check was performed. Null when no
    /// compatibility metadata was present on the skill, or no checker was supplied.
    /// </summary>
    public SkillCompatibilityResult? Compatibility { get; init; }

    /// <summary>Returns an allowed result, optionally with a compatibility detail.</summary>
    public static SkillAvailabilityResult Allow(SkillCompatibilityResult? compat = null)
        => new() { Allowed = true, Compatibility = compat };

    /// <summary>Returns a denied result with a mandatory reason.</summary>
    public static SkillAvailabilityResult Deny(string reason, SkillCompatibilityResult? compat = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Allowed = false, DenialReason = reason, Compatibility = compat };
    }
}

/// <summary>
/// Pure, injectable policy that decides whether a <see cref="Skill"/> row may appear in an
/// agent's system-prompt context. Rules are checked in order; the first failing check denies:
/// <list type="number">
///   <item>The skill must be enabled.</item>
///   <item>For a guest turn, the skill's visibility must grant access from the caller's circles
///         (deny-by-default; private or absent visibility is always denied).</item>
///   <item>When a <see cref="SkillCompatibilityChecker"/> is supplied AND the skill carries
///         explicit <see cref="SkillCompatibility"/> metadata, the compatibility verdict is
///         applied:
///         <list type="bullet">
///           <item><em>Incompatible</em> – skill is denied with the checker's reason(s).</item>
///           <item><em>Warning</em> – installation may retain the skill, but runtime use is denied
///                 because <see cref="SkillCompatibilityResult.CanRun"/> is false.</item>
///           <item><em>Compatible</em> – skill is allowed.</item>
///         </list>
///   </item>
/// </list>
/// Note on package materialization: full-package install (supporting files) on mobile is blocked
/// by <see cref="SkillCompatibilityChecker.CheckPackage"/> at install time (orchestrator-owned).
/// This policy gates runtime visibility only.
/// </summary>
public static class CircleSkillPolicy
{
    /// <summary>
    /// Evaluate whether <paramref name="skill"/> may appear in the agent's context for the
    /// current turn.
    /// </summary>
    /// <param name="skill">The skill to evaluate. Must not be null.</param>
    /// <param name="isOwner">True when this is an owner turn (private chat / own-thread).</param>
    /// <param name="guestCircles">
    /// The requesting contact's circles. Only consulted when <paramref name="isOwner"/> is false.
    /// Null is treated as empty (no circles).
    /// </param>
    /// <param name="checker">
    /// Optional device-compatibility checker. When null, compatibility metadata on the skill is
    /// ignored and the skill passes through rules 1–2 only. Pass
    /// <see cref="SkillCompatibilityChecker.ForCurrentDevice"/> in production.
    /// </param>
    public static SkillAvailabilityResult Evaluate(
        Skill skill,
        bool isOwner,
        IEnumerable<string>? guestCircles,
        SkillCompatibilityChecker? checker = null)
    {
        ArgumentNullException.ThrowIfNull(skill);

        // Rule 1 – disabled skills are never offered to any agent.
        if (!skill.Enabled)
            return SkillAvailabilityResult.Deny($"Skill '{skill.Name}' is disabled.");

        // Rule 2 – guest audience check (deny-by-default; only explicit grants allow).
        if (!isOwner)
        {
            var circles = guestCircles ?? Enumerable.Empty<string>();
            if (!AudiencePolicy.CanAccess(skill.Visibility, circles))
                return SkillAvailabilityResult.Deny(
                    $"Skill '{skill.Name}' is not shared with the caller's circles " +
                    $"(stored visibility: '{skill.Visibility ?? CapabilityAudience.PrivateVisibility}').");
        }

        // Rule 3 – device compatibility (only when checker and metadata are both present).
        if (checker is not null && skill.Compatibility is not null)
        {
            var compat = checker.Check(skill.Compatibility);
            if (!compat.CanRun)
                return SkillAvailabilityResult.Deny(
                    $"Skill '{skill.Name}' cannot run on this device: " +
                    string.Join("; ", compat.Reasons),
                    compat);
            return SkillAvailabilityResult.Allow(compat);
        }

        return SkillAvailabilityResult.Allow();
    }
}
