using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class MemoryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Normalize_CleansAndBoundsStoredFields()
    {
        var normalized = MemoryPolicy.Normalize(new MemoryItem
        {
            Id = "  memory-1  ",
            Title = "  Prefers   concise answers  ",
            Content = "  Keep replies concise.\r\nAvoid filler.  ",
            Category = "unknown",
            Origin = "unknown",
            Importance = double.NaN,
            Confidence = 2,
            Stability = -1,
            ReinforcementCount = 0,
            RecallCount = -10,
            SourceThreadId = "not\nvalid",
            SourceLineId = "line-1",
            CreatedAt = Now,
            UpdatedAt = Now.AddDays(-1),
            LastReinforcedAt = Now.AddDays(-2)
        });

        Assert.AreEqual("memory-1", normalized.Id);
        Assert.AreEqual("Prefers concise answers", normalized.Title);
        Assert.AreEqual("Keep replies concise.\nAvoid filler.", normalized.Content);
        Assert.AreEqual(MemoryCategories.PersonalFact, normalized.Category);
        Assert.AreEqual(MemoryOrigins.Inferred, normalized.Origin);
        Assert.AreEqual(0.65, normalized.Importance, 0.0001);
        Assert.AreEqual(1, normalized.Confidence, 0.0001);
        Assert.AreEqual(0, normalized.Stability, 0.0001);
        Assert.AreEqual(1, normalized.ReinforcementCount);
        Assert.AreEqual(0, normalized.RecallCount);
        Assert.IsNull(normalized.SourceThreadId);
        Assert.AreEqual("line-1", normalized.SourceLineId);
        Assert.AreEqual(Now, normalized.UpdatedAt);
        Assert.AreEqual(Now, normalized.LastReinforcedAt);
    }

    [TestMethod]
    public void SyncRoundTrip_ExcludesLocalRecallHistory()
    {
        var source = CreateMemory(
            "sync-1",
            "Preferred editor",
            "The owner prefers Visual Studio Code.",
            MemoryCategories.Preference,
            MemoryOrigins.Explicit);
        source.RecallCount = 14;
        source.LastRecalledAt = Now;

        var dto = MemoryPolicy.ToSync(source);
        var roundTrip = MemoryPolicy.FromSync(dto);

        Assert.AreEqual(source.Id, roundTrip.Id);
        Assert.AreEqual(source.Content, roundTrip.Content);
        Assert.AreEqual(0, roundTrip.RecallCount);
        Assert.IsNull(roundTrip.LastRecalledAt);
        Assert.IsNull(typeof(MemoryProjection).GetProperty(nameof(MemoryItem.RecallCount)));
        Assert.IsTrue(MemoryPolicy.IsValid(dto));
    }

    [TestMethod]
    public void SelectForPrompt_PrioritizesLexicallyRelevantMemory()
    {
        var relevant = CreateMemory(
            "relevant",
            "Vegetarian meals",
            "The owner prefers vegetarian meals when attending conferences.",
            MemoryCategories.Preference,
            MemoryOrigins.Inferred,
            importance: 0.52,
            stability: 0.75);
        var unrelated = CreateMemory(
            "unrelated",
            "Important tax constraint",
            "Keep all tax records for seven years.",
            MemoryCategories.Constraint,
            MemoryOrigins.Explicit,
            importance: 0.98,
            stability: 0.98);

        var selected = MemoryPolicy.SelectForPrompt(
            [unrelated, relevant],
            "What vegetarian conference meals should I order?",
            maxResults: 2,
            now: Now);

        Assert.HasCount(2, selected);
        Assert.AreEqual("relevant", selected[0].Id);
    }

    [TestMethod]
    public void SelectForPrompt_DoesNotExposeUnrelatedSensitiveMemory()
    {
        var sensitive = CreateMemory(
            "health",
            "Health",
            "The owner has depression.",
            MemoryCategories.PersonalFact,
            MemoryOrigins.Explicit,
            importance: 0.95,
            stability: 0.95);

        var unrelated = MemoryPolicy.SelectForPrompt(
            [sensitive],
            "Help refactor this database query.",
            now: Now);
        var relevant = MemoryPolicy.SelectForPrompt(
            [sensitive],
            "What have I said about my depression?",
            now: Now);

        Assert.AreEqual(0, unrelated.Count);
        Assert.AreEqual("health", relevant.Single().Id);
    }

    [TestMethod]
    public void GeneralSalience_RewardsExplicitReinforcedAndRecalledMemory()
    {
        var baseline = CreateMemory(
            "base",
            "Baseline",
            "A stable owner preference.",
            MemoryCategories.Preference,
            MemoryOrigins.Inferred,
            importance: 0.65,
            stability: 0.75);
        var reinforced = MemoryPolicy.Clone(baseline);
        reinforced.Id = "reinforced";
        reinforced.Origin = MemoryOrigins.Explicit;
        reinforced.ReinforcementCount = 16;
        reinforced.RecallCount = 12;

        Assert.IsTrue(
            MemoryPolicy.GeneralSalience(reinforced, Now)
            > MemoryPolicy.GeneralSalience(baseline, Now));
    }

    [TestMethod]
    public void FindSimilar_DeduplicatesParaphrasedMemory()
    {
        var existing = CreateMemory(
            "existing",
            "Concise answers",
            "The owner prefers concise answers with no preamble.",
            MemoryCategories.Preference,
            MemoryOrigins.Manual);

        var match = MemoryPolicy.FindSimilar(
            [existing],
            "Concise answers",
            "The owner prefers concise answers without a preamble.",
            MemoryCategories.Preference);
        var miss = MemoryPolicy.FindSimilar(
            [existing],
            "Favorite meal",
            "The owner enjoys mushroom risotto.",
            MemoryCategories.Preference);

        Assert.AreSame(existing, match);
        Assert.IsNull(miss);
    }

    [TestMethod]
    public void Evidence_RequiresExactOwnerTextQuoteAfterWhitespaceNormalization()
    {
        const string ownerText = "Please remember that I prefer   concise answers.";

        Assert.IsTrue(MemoryPolicy.EvidenceAppearsIn(ownerText, "I prefer concise answers"));
        Assert.IsFalse(MemoryPolicy.EvidenceAppearsIn(ownerText, "I prefer detailed answers"));
        Assert.IsFalse(MemoryPolicy.EvidenceAppearsIn(ownerText, "I"));
    }

    [TestMethod]
    public void RememberAndForgetIntent_RequireActualOwnerDirectives()
    {
        Assert.IsTrue(MemoryPolicy.HasExplicitRememberIntent("Remember that I have depression."));
        Assert.IsTrue(MemoryPolicy.HasExplicitRememberIntent("Please remember my preference."));
        Assert.IsFalse(MemoryPolicy.HasExplicitRememberIntent("I don't remember whether I mentioned depression."));
        Assert.IsFalse(MemoryPolicy.HasExplicitRememberIntent("Do you remember that I mentioned depression?"));
        Assert.IsTrue(MemoryPolicy.HasExplicitRememberIntentForEvidence(
            "Remember that I have depression.",
            "I have depression"));
        Assert.IsFalse(MemoryPolicy.HasExplicitRememberIntentForEvidence(
            "Remember that I prefer short replies. I have depression.",
            "I have depression"));

        Assert.IsTrue(MemoryPolicy.HasForgetIntent("Please forget my concise-answer preference."));
        Assert.IsTrue(MemoryPolicy.HasForgetIntent("Delete that memory."));
        Assert.IsFalse(MemoryPolicy.HasForgetIntent("I forget where I parked."));
        Assert.IsFalse(MemoryPolicy.HasForgetIntent("Why did you delete that memory?"));
    }

    [TestMethod]
    public void CredentialGuard_RejectsLabelsTokensCardsAndGovernmentIds()
    {
        Assert.IsTrue(MemoryPolicy.ContainsCredentialLikeData("My password is swordfish"));
        Assert.IsTrue(MemoryPolicy.ContainsCredentialLikeData("sk-proj-abcdefghijklmnop"));
        Assert.IsTrue(MemoryPolicy.ContainsCredentialLikeData(
            "eyJabcdefghijk.abcdefghijklmnop.qrstuvwxyz12345"));
        Assert.IsTrue(MemoryPolicy.ContainsCredentialLikeData("4242 4242 4242 4242"));
        Assert.IsTrue(MemoryPolicy.ContainsCredentialLikeData("123-45-6789"));
        Assert.IsTrue(MemoryPolicy.ContainsCredentialLikeData("My passport number is 123456789"));
        Assert.IsFalse(MemoryPolicy.ContainsCredentialLikeData("Use concise answers and short examples."));
        Assert.IsFalse(MemoryPolicy.ContainsCredentialLikeData("Skateboarding is fun."));
    }

    [TestMethod]
    public void SensitiveGuard_RecognizesHealthIdentityAndStreetAddressWithoutSubstringNoise()
    {
        Assert.IsTrue(MemoryPolicy.ContainsSensitivePersonalData("I have depression."));
        Assert.IsTrue(MemoryPolicy.ContainsSensitivePersonalData("I am Jewish."));
        Assert.IsTrue(MemoryPolicy.ContainsSensitivePersonalData("I live at 123 Main Street."));
        Assert.IsFalse(MemoryPolicy.ContainsSensitivePersonalData("The API gateway is stable."));
        Assert.IsFalse(MemoryPolicy.ContainsSensitivePersonalData("The first-aid kit is upstairs."));
    }

    [TestMethod]
    public void IsValid_RejectsOutOfRangeOrChronologicallyInvalidSyncData()
    {
        var valid = MemoryPolicy.ToSync(CreateMemory(
            "valid",
            "Valid",
            "A valid reliable preference.",
            MemoryCategories.Preference,
            MemoryOrigins.Explicit));
        var invalidScore = valid with { Importance = 1.1 };
        var invalidTime = valid with { UpdatedAt = valid.CreatedAt.AddSeconds(-1) };
        var secret = valid with { Content = "My passport number is 123456789." };
        var inferredSensitive = valid with
        {
            Origin = MemoryOrigins.Inferred,
            Content = "The owner has depression."
        };

        Assert.IsFalse(MemoryPolicy.IsValid(invalidScore));
        Assert.IsFalse(MemoryPolicy.IsValid(invalidTime));
        Assert.IsFalse(MemoryPolicy.IsValid(secret));
        Assert.IsFalse(MemoryPolicy.IsValid(inferredSensitive));
    }

    private static MemoryItem CreateMemory(
        string id,
        string title,
        string content,
        string category,
        string origin,
        double importance = 0.7,
        double stability = 0.8)
        => new()
        {
            Id = id,
            Title = title,
            Content = content,
            Category = category,
            Origin = origin,
            Importance = importance,
            Confidence = 0.9,
            Stability = stability,
            ReinforcementCount = 1,
            CreatedAt = Now.AddDays(-30),
            UpdatedAt = Now.AddDays(-1),
            LastReinforcedAt = Now.AddDays(-1)
        };
}
