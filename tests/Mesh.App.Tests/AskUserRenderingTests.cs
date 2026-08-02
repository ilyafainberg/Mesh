using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

/// <summary>
/// Source-inspection tests for the ask-user bubble component and its scoped CSS.
///
/// These tests follow the same repository-source-file inspection pattern used in
/// <see cref="ImagePreviewTests"/> and <see cref="WidgetRenderingSecurityTests"/>:
/// they read the .razor and .razor.css files directly and assert that the markup /
/// style text satisfies the expected accessibility, layout, and interaction contracts.
/// No MAUI / bunit dependency is needed — the tests are pure MSTest on net10.0.
/// </summary>
[TestClass]
public sealed class AskUserRenderingTests
{
    // ── Source loading ───────────────────────────────────────────────────────

    private static string Markup =>
        ReadRepoFile("src", "Mesh.App", "Components", "AskUserBubble.razor");

    private static string Styles =>
        ReadRepoFile("src", "Mesh.App", "Components", "AskUserBubble.razor.css");

    // ── Root element / ARIA semantics ────────────────────────────────────────

    [TestMethod]
    public void RootElement_HasGroupRoleAndAriaLabel()
    {
        StringAssert.Contains(Markup, "role=\"group\"");
        StringAssert.Contains(Markup, "aria-label=\"Assistant question\"");
    }

    [TestMethod]
    public void RootElement_IdBoundToPromptId()
    {
        StringAssert.Contains(Markup, "id=\"@($\"ask-{Bubble.PromptId}\")\"");
    }

    [TestMethod]
    public void DecorativeIcons_HaveAriaHidden()
    {
        // The question-mark icon and the check-circle icon must not be read aloud.
        StringAssert.Contains(Markup, "bi-patch-question\" aria-hidden=\"true\"");
        StringAssert.Contains(Markup, "bi-check-circle\" aria-hidden=\"true\"");
    }

    // ── Option buttons ───────────────────────────────────────────────────────

    [TestMethod]
    public void Options_AreNativeButtonElementsWithType()
    {
        StringAssert.Contains(Markup, "<button type=\"button\"");
    }

    [TestMethod]
    public void Options_HaveDisabledBindingOnIsInteractiveAndIsResolving()
    {
        // Prevents double-submit and blocks settled-state interaction.
        StringAssert.Contains(Markup, "disabled=\"@(!Bubble.IsInteractive || IsResolving)\"");
    }

    [TestMethod]
    public void Options_HaveAriaPressedBinding()
    {
        StringAssert.Contains(Markup, "aria-pressed=\"@(option.IsSelected ? \"true\" : \"false\")\"");
    }

    [TestMethod]
    public void Options_HaveAriaLabelFromOptionLabelHelper()
    {
        StringAssert.Contains(Markup, "aria-label=\"@OptionLabel(option)\"");
    }

    [TestMethod]
    public void Options_HaveKeyDirectiveForStableRerender()
    {
        StringAssert.Contains(Markup, "@key=\"option.Id\"");
    }

    [TestMethod]
    public void Options_TitleSpanPresent()
    {
        StringAssert.Contains(Markup, "class=\"ask-user-option-title\"");
    }

    [TestMethod]
    public void Options_BadgeIsConditionallyRenderedForRecommended()
    {
        StringAssert.Contains(Markup, "option.IsRecommended");
        StringAssert.Contains(Markup, "class=\"ask-user-badge\" aria-hidden=\"true\"");
    }

    [TestMethod]
    public void Options_DescriptionConditionallyRendered()
    {
        StringAssert.Contains(Markup, "string.IsNullOrWhiteSpace(option.Description)");
        StringAssert.Contains(Markup, "class=\"ask-user-option-desc\"");
    }

    // ── Status region ────────────────────────────────────────────────────────

    [TestMethod]
    public void StatusRegion_HasAriaLivePoliteAndAriaAtomic()
    {
        StringAssert.Contains(Markup, "aria-live=\"polite\"");
        StringAssert.Contains(Markup, "aria-atomic=\"true\"");
    }

    [TestMethod]
    public void StatusRegion_CoversPendingState()
    {
        // The ellipsis character (…) is part of the label; Assert Contains the core text.
        Assert.IsTrue(Markup.Contains("Waiting for your choice"), "Expected pending-state label in markup.");
    }

    [TestMethod]
    public void StatusRegion_CoversAnsweredState()
    {
        StringAssert.Contains(Markup, "You chose @Bubble.SelectedOptionTitle");
    }

    [TestMethod]
    public void StatusRegion_CoversExpiredState()
    {
        StringAssert.Contains(Markup, "This question expired.");
    }

    [TestMethod]
    public void StatusRegion_CoversCancelledState()
    {
        StringAssert.Contains(Markup, "This question was cancelled.");
    }

    // ── Parameters ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Component_ExposesOnSelectCallback()
    {
        StringAssert.Contains(Markup, "EventCallback<string> OnSelect");
    }

    [TestMethod]
    public void Component_ExposesIsResolvingFlag()
    {
        StringAssert.Contains(Markup, "bool IsResolving");
    }

    [TestMethod]
    public void Component_ExposesIsFocusedFlag()
    {
        StringAssert.Contains(Markup, "bool IsFocused");
    }

    // ── CSS layout correctness (the primary bug fix) ─────────────────────────

    [TestMethod]
    public void Css_BubbleHasWidthFull_NotAlignSelfFlexStart()
    {
        // The root-cause bug: align-self: flex-start causes the bubble to shrink
        // to content width, making width:100% buttons have no concrete parent.
        Assert.IsFalse(
            Styles.Contains("align-self: flex-start", StringComparison.Ordinal),
            "align-self: flex-start must NOT appear in AskUserBubble.razor.css — it is the root cause of broken button widths.");

        // Fix: width:100% gives the bubble a concrete layout width.
        StringAssert.Contains(Styles, "width: 100%;");
    }

    [TestMethod]
    public void Css_BubbleHasMaxWidth680px()
    {
        StringAssert.Contains(Styles, "max-width: 680px;");
    }

    [TestMethod]
    public void Css_BubbleHasMinWidthZero_GuardAgainstFlexOverflow()
    {
        StringAssert.Contains(Styles, "min-width: 0;");
    }

    [TestMethod]
    public void Css_OptionButtonHasMobileMinHeight48px()
    {
        // WCAG 2.5.5 / Android minimum touch-target height.
        StringAssert.Contains(Styles, "min-height: 48px;");
    }

    [TestMethod]
    public void Css_NarrowMobileMediaQuery_IncreasesMinHeightTo52px()
    {
        // Extra tap-target size on small screens.
        StringAssert.Contains(Styles, "@media (max-width: 640px)");
        StringAssert.Contains(Styles, "min-height: 52px;");
    }

    [TestMethod]
    public void Css_OptionButtonHasWidthFull()
    {
        // Options must fill the bubble's width — only correct after the align-self bug is fixed.
        StringAssert.Contains(Styles, "width: 100%;");
    }

    [TestMethod]
    public void Css_OptionText_IsWidthBoundAndWraps()
    {
        StringAssert.Contains(Styles, ".ask-user-option-title");
        StringAssert.Contains(Styles, ".ask-user-option-desc");
        StringAssert.Contains(Styles, "max-width: 100%;");
        StringAssert.Contains(Styles, "white-space: normal;");
        StringAssert.Contains(Styles, "overflow-wrap: break-word;");
    }

    [TestMethod]
    public void Css_OverflowWrapBreakWordFallbackPresent()
    {
        // Older Android WebViews don't support overflow-wrap: anywhere;
        // word-break: break-word is the universal fallback.
        StringAssert.Contains(Styles, "word-break: break-word;");
    }

    [TestMethod]
    public void Css_OptionsFlex_IsColumn()
    {
        StringAssert.Contains(Styles, "flex-direction: column;");
    }

    [TestMethod]
    public void Css_FocusVisibleOutlinePresent()
    {
        StringAssert.Contains(Styles, ":focus-visible");
        StringAssert.Contains(Styles, "outline: 2px solid");
    }

    [TestMethod]
    public void Css_SettledStateOverridesBackground()
    {
        StringAssert.Contains(Styles, ".ask-user-bubble.is-settled");
    }

    [TestMethod]
    public void Css_FocusedStateOutline()
    {
        StringAssert.Contains(Styles, ".ask-user-bubble.is-focused");
    }

    [TestMethod]
    public void Css_SelectedOptionUsesHighContrastBrandColor()
    {
        StringAssert.Contains(Styles, ".ask-user-option.is-selected");
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static string ReadRepoFile(params string[] parts)
    {
        var relativePath = Path.Combine(parts);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not locate repository file {relativePath}.");
    }
}
