using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Exhaustive matrix tests for <see cref="CircleToolPolicy"/>. All checks are pure — no I/O, no
/// platform APIs, no MAUI. Each section corresponds to one policy rule (numbered to match the
/// XML doc on <see cref="CircleToolPolicy"/>).
/// </summary>
[TestClass]
public sealed class CircleToolPolicyTests
{
    // ────────────────────────────── helpers ──────────────────────────────────────

    private static LocalToolSetting Enabled(string visibility = "private") => new()
    {
        Enabled = true,
        Visibility = visibility
    };

    private static LocalToolSetting Disabled() => new() { Enabled = false };

    private static string CircleVis(string circle) => $"shared:{circle}";
    private const string AllContacts = "public";

    // ──────────────────────────── Rule 1: disabled ────────────────────────────────

    [TestMethod]
    public void NullSetting_IsDenied_WithReason()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, setting: null, isOwner: true,
            guestCircles: null, isMobile: false);

        Assert.IsFalse(result.Allowed);
        Assert.IsNotNull(result.DenialReason);
        StringAssert.Contains(result.DenialReason, "WebSearch");
    }

    [TestMethod]
    public void DisabledSetting_IsDenied_ForOwner()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.PowerShell, Disabled(), isOwner: true,
            guestCircles: null, isMobile: false);

        Assert.IsFalse(result.Allowed);
    }

    [TestMethod]
    public void DisabledSetting_IsDenied_ForGuest_EvenWhenCircleMatches()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Disabled(), isOwner: false,
            guestCircles: ["Trusted"], isMobile: false);

        Assert.IsFalse(result.Allowed);
    }

    // ────── Rule 2: mobile blocks all desktop-only tools (fail-closed) ──────────

    [DataTestMethod]
    [DataRow(LocalToolKind.PowerShell)]
    [DataRow(LocalToolKind.Cmd)]
    [DataRow(LocalToolKind.Python)]
    [DataRow(LocalToolKind.CSharpScript)]
    [DataRow(LocalToolKind.Browser)]
    [DataRow(LocalToolKind.HeadlessBrowser)]
    [DataRow(LocalToolKind.WorkIq)]
    [DataRow(LocalToolKind.FileSystem)]
    public void DesktopOnlyTool_OnMobile_IsDenied_EvenIfOwnerAndEnabled(LocalToolKind kind)
    {
        // The profile may have the tool enabled from a desktop device that synced the profile.
        // The policy must still fail closed.
        var result = CircleToolPolicy.Evaluate(
            kind, Enabled(), isOwner: true,
            guestCircles: null, isMobile: true);

        Assert.IsFalse(result.Allowed,
            $"{kind} must be denied on mobile even when profile has it enabled.");
        StringAssert.Contains(result.DenialReason, "mobile",
            $"Denial reason for {kind} must mention 'mobile'.");
    }

    [DataTestMethod]
    [DataRow(LocalToolKind.PowerShell)]
    [DataRow(LocalToolKind.FileSystem)]
    public void DesktopOnlyTool_OnMobile_DeniedForGuest_Too(LocalToolKind kind)
    {
        // Mobile block is checked before the guest audience check.
        var result = CircleToolPolicy.Evaluate(
            kind, Enabled(AllContacts), isOwner: false,
            guestCircles: ["Work"], isMobile: true);

        Assert.IsFalse(result.Allowed,
            $"{kind} must be denied on mobile for guests as well.");
    }

    [DataTestMethod]
    [DataRow(LocalToolKind.WebSearch)]
    [DataRow(LocalToolKind.Geolocation)]
    public void MobileAvailableTools_OnMobile_Owner_AreAllowed(LocalToolKind kind)
    {
        var result = CircleToolPolicy.Evaluate(
            kind, Enabled(), isOwner: true,
            guestCircles: null, isMobile: true);

        Assert.IsTrue(result.Allowed,
            $"{kind} is valid on mobile and must not be blocked.");
    }

    [TestMethod]
    public void DesktopOnlyTool_OnDesktop_IsAllowed_ForOwner()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.PowerShell, Enabled(), isOwner: true,
            guestCircles: null, isMobile: false);

        Assert.IsTrue(result.Allowed);
    }

    // ── Rule 3: MeshData is always owner-only (hard privacy boundary) ────────────

    [TestMethod]
    public void MeshData_Owner_IsAllowed()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.MeshData, Enabled(AllContacts), isOwner: true,
            guestCircles: null, isMobile: false);

        Assert.IsTrue(result.Allowed);
    }

    [TestMethod]
    public void MeshData_Guest_IsDenied_RegardlessOfVisibility()
    {
        // Even "public" visibility must not let a guest reach MeshData.
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.MeshData, Enabled(AllContacts), isOwner: false,
            guestCircles: ["Trusted", "Work"], isMobile: false);

        Assert.IsFalse(result.Allowed,
            "MeshData must always be denied for guests.");
        StringAssert.Contains(result.DenialReason, "owner-only",
            "Denial reason must explicitly say owner-only.");
    }

    [TestMethod]
    public void MeshData_Guest_CircleShared_IsDenied()
    {
        // A profile that mistakenly stores a circle-shared visibility for MeshData must still deny.
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.MeshData, Enabled(CircleVis("Trusted")), isOwner: false,
            guestCircles: ["Trusted"], isMobile: false);

        Assert.IsFalse(result.Allowed,
            "MeshData must be denied for guests even when the circle matches.");
    }

    // ── Rule 4: guest audience check (deny-by-default) ───────────────────────────

    [TestMethod]
    public void Guest_PrivateVisibility_IsDenied()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Enabled("private"), isOwner: false,
            guestCircles: ["Trusted"], isMobile: false);

        Assert.IsFalse(result.Allowed);
        StringAssert.Contains(result.DenialReason, "private");
    }

    [TestMethod]
    public void Guest_NullCircles_IsDenied_WhenVisibilityIsSelectedCircle()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Enabled(CircleVis("Trusted")), isOwner: false,
            guestCircles: null, isMobile: false);

        Assert.IsFalse(result.Allowed);
    }

    [TestMethod]
    public void Guest_EmptyCircles_IsDenied_WhenVisibilityIsSelectedCircle()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Enabled(CircleVis("Work")), isOwner: false,
            guestCircles: [], isMobile: false);

        Assert.IsFalse(result.Allowed);
    }

    [TestMethod]
    public void Guest_MatchingCircle_IsAllowed()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Enabled(CircleVis("Trusted")), isOwner: false,
            guestCircles: ["Trusted"], isMobile: false);

        Assert.IsTrue(result.Allowed);
    }

    [TestMethod]
    public void Guest_NonMatchingCircle_IsDenied_WithExplicitReason()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Enabled(CircleVis("Work")), isOwner: false,
            guestCircles: ["Friends"], isMobile: false);

        Assert.IsFalse(result.Allowed);
        Assert.IsNotNull(result.DenialReason);
        // The reason must identify the tool and mention the stored visibility.
        StringAssert.Contains(result.DenialReason, "WebSearch");
        StringAssert.Contains(result.DenialReason, "Work");
    }

    [TestMethod]
    public void Guest_AllAllowedContacts_IsAllowed()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Enabled(AllContacts), isOwner: false,
            guestCircles: ["Friends"], isMobile: false);

        Assert.IsTrue(result.Allowed);
    }

    [TestMethod]
    public void Guest_AllAllowedContacts_IsAllowed_WithNoCircles()
    {
        // AllAllowedContacts must not require the caller to be in any circle.
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Enabled(AllContacts), isOwner: false,
            guestCircles: null, isMobile: false);

        Assert.IsTrue(result.Allowed,
            "AllAllowedContacts visibility must grant access even without circles.");
    }

    [TestMethod]
    public void Guest_MultiCircleVisibility_MatchesAnyMembership()
    {
        var multiVis = CapabilityAudience.ForCircles(["Work", "Trusted"]).ToVisibility();
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.Geolocation, Enabled(multiVis), isOwner: false,
            guestCircles: ["Friends", "Work"], isMobile: false);

        Assert.IsTrue(result.Allowed, "Caller is in 'Work' which is in the multi-circle set.");
    }

    [TestMethod]
    public void Guest_MultiCircleVisibility_NoIntersection_IsDenied()
    {
        var multiVis = CapabilityAudience.ForCircles(["Work", "Trusted"]).ToVisibility();
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.Geolocation, Enabled(multiVis), isOwner: false,
            guestCircles: ["Friends"], isMobile: false);

        Assert.IsFalse(result.Allowed);
    }

    // ── Owner bypass (no audience check) ─────────────────────────────────────────

    [TestMethod]
    public void Owner_PrivateVisibility_IsAllowed_NoCircleCheck()
    {
        // The owner's visibility is "private" by default; the owner must still get the tool.
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.Geolocation, Enabled("private"), isOwner: true,
            guestCircles: null, isMobile: false);

        Assert.IsTrue(result.Allowed,
            "Owner must always get access to enabled tools regardless of visibility.");
    }

    [TestMethod]
    public void Owner_NoCirclesNeeded_EvenWhenGuestCirclesSupplied()
    {
        // Owner context: guestCircles parameter must be ignored.
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.Geolocation, Enabled("private"), isOwner: true,
            guestCircles: [], isMobile: false);

        Assert.IsTrue(result.Allowed);
    }

    // ── Approval-level is preserved (pass-through) ───────────────────────────────

    [TestMethod]
    public void AllowedResult_HasNullDenialReason()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, Enabled(AllContacts), isOwner: false,
            guestCircles: ["Friends"], isMobile: false);

        Assert.IsTrue(result.Allowed);
        Assert.IsNull(result.DenialReason, "Allowed result must carry no denial reason.");
    }

    [TestMethod]
    public void DeniedResult_HasNonNullDenialReason()
    {
        var result = CircleToolPolicy.Evaluate(
            LocalToolKind.WebSearch, setting: null, isOwner: true,
            guestCircles: null, isMobile: false);

        Assert.IsFalse(result.Allowed);
        Assert.IsNotNull(result.DenialReason, "Denied result must always carry a reason.");
        Assert.IsTrue(result.DenialReason.Length > 0);
    }
}
