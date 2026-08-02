using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Exhaustive matrix tests for <see cref="CircleSkillPolicy"/>. All checks are pure — no I/O, no
/// platform APIs, no MAUI. Sections correspond to the policy rules in order.
/// </summary>
[TestClass]
public sealed class CircleSkillPolicyTests
{
    // ────────────────────────────── helpers ──────────────────────────────────────

    private static Skill MakeSkill(
        string name = "TestSkill",
        bool enabled = true,
        string visibility = "private",
        SkillCompatibility? compat = null)
        => new()
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Enabled = enabled,
            Visibility = visibility,
            Compatibility = compat
        };

    private static string CircleVis(string circle) => $"shared:{circle}";
    private const string AllContacts = "public";

    private sealed class FixedProbe : ICliToolProbe
    {
        private readonly HashSet<string> _available;
        public FixedProbe(params string[] available)
            => _available = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        public bool IsAvailable(string tool) => _available.Contains(tool);
    }

    private static SkillCompatibilityChecker Desktop(SkillOperatingSystems os, params string[] cliPresent)
        => new(os, isMobile: false, cliProbe: new FixedProbe(cliPresent));

    private static SkillCompatibilityChecker Mobile(SkillOperatingSystems os)
        => new(os, isMobile: true);

    // ──────────────────────────── Rule 1: disabled ────────────────────────────────

    [TestMethod]
    public void DisabledSkill_IsDenied_ForOwner()
    {
        var skill = MakeSkill(enabled: false, visibility: AllContacts);
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null);

        Assert.IsFalse(result.Allowed);
        StringAssert.Contains(result.DenialReason, "disabled");
        StringAssert.Contains(result.DenialReason, skill.Name);
    }

    [TestMethod]
    public void DisabledSkill_IsDenied_ForGuest_EvenWithMatchingCircle()
    {
        var skill = MakeSkill(enabled: false, visibility: CircleVis("Trusted"));
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: ["Trusted"]);

        Assert.IsFalse(result.Allowed);
    }

    // ──────────────── Rule 2: guest audience check (deny-by-default) ─────────────

    [TestMethod]
    public void Guest_PrivateSkill_IsDenied_WithVisibilityInReason()
    {
        var skill = MakeSkill(visibility: "private");
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: ["Trusted"]);

        Assert.IsFalse(result.Allowed);
        StringAssert.Contains(result.DenialReason, "private");
    }

    [TestMethod]
    public void Guest_NullCircles_IsDenied_ForCircleSharedSkill()
    {
        var skill = MakeSkill(visibility: CircleVis("Work"));
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: null);

        Assert.IsFalse(result.Allowed);
    }

    [TestMethod]
    public void Guest_EmptyCircles_IsDenied_ForCircleSharedSkill()
    {
        var skill = MakeSkill(visibility: CircleVis("Work"));
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: []);

        Assert.IsFalse(result.Allowed);
    }

    [TestMethod]
    public void Guest_MatchingCircle_IsAllowed()
    {
        var skill = MakeSkill(visibility: CircleVis("Trusted"));
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: ["Trusted"]);

        Assert.IsTrue(result.Allowed);
    }

    [TestMethod]
    public void Guest_NonMatchingCircle_IsDenied_WithSkillNameInReason()
    {
        var skill = MakeSkill(name: "CodeReview", visibility: CircleVis("Work"));
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: ["Friends"]);

        Assert.IsFalse(result.Allowed);
        Assert.IsNotNull(result.DenialReason);
        StringAssert.Contains(result.DenialReason, "CodeReview");
        StringAssert.Contains(result.DenialReason, "Work");
    }

    [TestMethod]
    public void Guest_AllAllowedContacts_IsAllowed_WithoutAnyCircle()
    {
        // AllAllowedContacts must grant access even when the caller is in no circle.
        var skill = MakeSkill(visibility: AllContacts);
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: null);

        Assert.IsTrue(result.Allowed,
            "AllAllowedContacts visibility must not require the caller to be in any circle.");
    }

    [TestMethod]
    public void Guest_AllAllowedContacts_IsAllowed_WithCircles()
    {
        var skill = MakeSkill(visibility: AllContacts);
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: ["Work"]);

        Assert.IsTrue(result.Allowed);
    }

    [TestMethod]
    public void Guest_MultiCircleVisibility_MatchesAnyMembership()
    {
        var vis = CapabilityAudience.ForCircles(["Work", "Trusted"]).ToVisibility();
        var skill = MakeSkill(visibility: vis);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false,
            guestCircles: ["Friends", "Trusted"]);

        Assert.IsTrue(result.Allowed, "Caller is in 'Trusted' which is in the multi-circle set.");
    }

    [TestMethod]
    public void Guest_MultiCircleVisibility_NoIntersection_IsDenied()
    {
        var vis = CapabilityAudience.ForCircles(["Work", "Trusted"]).ToVisibility();
        var skill = MakeSkill(visibility: vis);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false,
            guestCircles: ["Friends"]);

        Assert.IsFalse(result.Allowed);
    }

    // ── Owner bypass ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Owner_PrivateSkill_IsAllowed_NoCircleCheck()
    {
        // Skills default to private; owner must always see their own enabled skills.
        var skill = MakeSkill(visibility: "private");
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null);

        Assert.IsTrue(result.Allowed,
            "Owner must see enabled skills regardless of visibility.");
    }

    [TestMethod]
    public void Owner_IgnoresGuestCirclesParam()
    {
        var skill = MakeSkill(visibility: "private");
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: []);

        Assert.IsTrue(result.Allowed);
    }

    // ── Rule 3: no checker supplied → compat is ignored ──────────────────────────

    [TestMethod]
    public void NoChecker_SkillWithIncompatibleMetadata_IsAllowed()
    {
        // Without a checker, compatibility metadata must not block the skill.
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.Windows,
            DeviceClass = SkillDeviceClass.Desktop
        };
        var skill = MakeSkill(compat: compat);
        // Simulate running on macOS, checker not provided
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker: null);

        Assert.IsTrue(result.Allowed);
        Assert.IsNull(result.Compatibility, "No checker → no compatibility result produced.");
    }

    [TestMethod]
    public void NullCompatibilityOnSkill_WithChecker_IsAllowed()
    {
        // A skill with no compatibility metadata is treated as universal Skill.md-only: always ok.
        var skill = MakeSkill(); // Compatibility = null
        var checker = Desktop(SkillOperatingSystems.Windows);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker);

        Assert.IsTrue(result.Allowed);
        Assert.IsNull(result.Compatibility);
    }

    // ── Rule 3: checker supplied, Compatible verdict ──────────────────────────────

    [TestMethod]
    public void Compatible_WindowsSkill_OnWindows_IsAllowed_WithCompatResult()
    {
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.Windows,
            DeviceClass = SkillDeviceClass.Desktop
        };
        var skill = MakeSkill(compat: compat);
        var checker = Desktop(SkillOperatingSystems.Windows);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker);

        Assert.IsTrue(result.Allowed);
        Assert.IsNotNull(result.Compatibility);
        Assert.AreEqual(SkillCompatibilityLevel.Compatible, result.Compatibility.Level);
    }

    // ── Rule 3: checker supplied, Incompatible verdict ────────────────────────────

    [TestMethod]
    public void Incompatible_OsMismatch_IsDenied_WithCompatResult()
    {
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.Linux,
            DeviceClass = SkillDeviceClass.Desktop
        };
        var skill = MakeSkill(compat: compat);
        var checker = Desktop(SkillOperatingSystems.Windows);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker);

        Assert.IsFalse(result.Allowed);
        Assert.IsNotNull(result.Compatibility);
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Compatibility.Level);
        StringAssert.Contains(result.DenialReason, skill.Name,
            "Denial reason must name the skill.");
    }

    [TestMethod]
    public void Incompatible_MobileDeviceClassOnDesktop_IsDenied()
    {
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.All,
            DeviceClass = SkillDeviceClass.Mobile
        };
        var skill = MakeSkill(compat: compat);
        var checker = Desktop(SkillOperatingSystems.Windows);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker);

        Assert.IsFalse(result.Allowed);
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Compatibility?.Level);
    }

    [TestMethod]
    public void Incompatible_DesktopClassOnMobile_IsDenied()
    {
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.AllDesktop,
            DeviceClass = SkillDeviceClass.Desktop
        };
        var skill = MakeSkill(compat: compat);
        var checker = Mobile(SkillOperatingSystems.IOS);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker);

        Assert.IsFalse(result.Allowed);
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Compatibility?.Level);
    }

    [TestMethod]
    public void Incompatible_SkillRequiringCli_OnMobile_IsDenied()
    {
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.All,
            DeviceClass = SkillDeviceClass.Universal,
            RequiredCliTools = new List<string> { "git" }
        };
        var skill = MakeSkill(compat: compat);
        var checker = Mobile(SkillOperatingSystems.Android);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker);

        Assert.IsFalse(result.Allowed,
            "CLI-requiring skills must be incompatible on mobile.");
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Compatibility?.Level);
    }

    // ── Rule 3: checker supplied, Warning verdict ─────────────────────────────────

    [TestMethod]
    public void Warning_MissingCli_IsDenied_AtRuntime()
    {
        // Missing CLI tool: installation can warn, but runtime prompt hydration must fail closed.
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.Windows,
            DeviceClass = SkillDeviceClass.Desktop,
            RequiredCliTools = new List<string> { "docker" }
        };
        var skill = MakeSkill(compat: compat);
        var checker = Desktop(SkillOperatingSystems.Windows /* docker NOT available */);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker);

        Assert.IsFalse(result.Allowed,
            "A skill that cannot run must not appear in the active agent context.");
        Assert.IsNotNull(result.Compatibility);
        Assert.AreEqual(SkillCompatibilityLevel.Warning, result.Compatibility.Level);
        Assert.IsFalse(result.Compatibility.CanRun,
            "CanRun must be false when a required CLI tool is missing.");
        CollectionAssert.Contains(result.Compatibility.MissingCliTools.ToList(), "docker");
    }

    [TestMethod]
    public void Warning_PartialCli_ReportsOnlyMissingTools()
    {
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.Linux,
            DeviceClass = SkillDeviceClass.Desktop,
            RequiredCliTools = new List<string> { "git", "docker" }
        };
        var skill = MakeSkill(compat: compat);
        var checker = Desktop(SkillOperatingSystems.Linux, "git" /* docker missing */);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null, checker);

        Assert.IsFalse(result.Allowed);
        Assert.AreEqual(SkillCompatibilityLevel.Warning, result.Compatibility?.Level);
        CollectionAssert.AreEqual(
            new[] { "docker" },
            result.Compatibility!.MissingCliTools.ToArray());
    }

    // ── Circle check runs BEFORE compat check ────────────────────────────────────

    [TestMethod]
    public void Guest_CircleDenied_BeforeCompatibilityIsEvaluated()
    {
        // Even if the skill is incompatible, the circle check fires first and produces a circle
        // denial reason (not a compatibility result).
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.Windows,
            DeviceClass = SkillDeviceClass.Desktop,
            RequiredCliTools = new List<string> { "docker" }
        };
        var skill = MakeSkill(visibility: CircleVis("Work"), compat: compat);
        var checker = Desktop(SkillOperatingSystems.Windows);

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false,
            guestCircles: ["Friends"], checker);

        Assert.IsFalse(result.Allowed);
        // No compatibility object: circle check short-circuited before compat.
        Assert.IsNull(result.Compatibility,
            "Compatibility check must not run when circle access is already denied.");
    }

    [TestMethod]
    public void Guest_CircleAllowed_ThenCompatChecked()
    {
        // Circle grants access; the compatibility check fires and incompatible verdict is applied.
        var compat = new SkillCompatibility
        {
            OperatingSystems = SkillOperatingSystems.Linux,
            DeviceClass = SkillDeviceClass.Desktop
        };
        var skill = MakeSkill(visibility: CircleVis("Work"), compat: compat);
        var checker = Desktop(SkillOperatingSystems.Windows); // OS mismatch

        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false,
            guestCircles: ["Work"], checker);

        Assert.IsFalse(result.Allowed);
        Assert.IsNotNull(result.Compatibility,
            "Compatibility result must be present when circle access was granted but compat failed.");
        Assert.AreEqual(SkillCompatibilityLevel.Incompatible, result.Compatibility.Level);
    }

    // ── Null-guard ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void NullSkill_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            CircleSkillPolicy.Evaluate(null!, isOwner: true, guestCircles: null));
    }

    // ── Invariant: allowed result has null denial reason ─────────────────────────

    [TestMethod]
    public void AllowedResult_HasNullDenialReason()
    {
        var skill = MakeSkill(visibility: AllContacts);
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: false, guestCircles: null);

        Assert.IsTrue(result.Allowed);
        Assert.IsNull(result.DenialReason, "Allowed result must carry no denial reason.");
    }

    [TestMethod]
    public void DeniedResult_HasNonNullDenialReason()
    {
        var skill = MakeSkill(enabled: false);
        var result = CircleSkillPolicy.Evaluate(skill, isOwner: true, guestCircles: null);

        Assert.IsFalse(result.Allowed);
        Assert.IsNotNull(result.DenialReason, "Denied result must always carry a reason.");
        Assert.IsTrue(result.DenialReason.Length > 0);
    }
}
