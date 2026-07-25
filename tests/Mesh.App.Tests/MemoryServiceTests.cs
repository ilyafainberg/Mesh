using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class MemoryServiceTests
{
    [TestMethod]
    public void ManualEditAndDelete_RejectAChangedEditorBaseline()
    {
        var state = new FakeMemoryState();
        state.Profile.Memories.Add(CreateMemory(
            "memory-1",
            "Concise answers",
            "The owner prefers concise answers.",
            MemoryOrigins.Explicit));
        var service = new MemoryService(state);
        var baseline = MemoryPolicy.Clone(state.Profile.Memories.Single());
        state.Profile.Memories[0].Content = "A newer synchronized value.";
        state.Profile.Memories[0].UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);

        Assert.ThrowsExactly<InvalidOperationException>(() => service.SaveManual(
            "memory-1",
            "Concise answers",
            "Stale editor value.",
            MemoryCategories.Preference,
            baseline));
        Assert.IsFalse(service.DeleteManual("memory-1", baseline));
        Assert.AreEqual("A newer synchronized value.", state.Profile.Memories.Single().Content);
    }

    [TestMethod]
    public void TurnTools_AreInternalAndPromptStatesThePrivacyBoundary()
    {
        var state = new FakeMemoryState();
        var service = new MemoryService(state);
        using var turn = service.BeginTurn("topic-1", "line-1", "Help me plan dinner.");

        CollectionAssert.AreEquivalent(
            new[] { "recall_memories", "remember_memory", "forget_memory" },
            turn.Tools.Select(tool => tool.Name).ToArray());
        Assert.IsTrue(turn.Tools.All(tool => tool.IsInternal));
        StringAssert.Contains(turn.BuildSystemPrompt(), "never available in Messages");
        StringAssert.Contains(turn.BuildSystemPrompt(), "exact evidence quote");
    }

    [TestMethod]
    public async Task Remember_IsStagedUntilCommitAndCancelledTurnDiscardsIt()
    {
        var state = new FakeMemoryState();
        var service = new MemoryService(state);

        using (var cancelled = service.BeginTurn(
                   "topic-1", "line-1", "I prefer concise answers."))
        {
            var result = await ExecuteAsync(cancelled, "remember_memory", """
                {
                  "title": "Concise answers",
                  "content": "The owner prefers concise answers.",
                  "category": "preference",
                  "evidence": "I prefer concise answers",
                  "importance": 0.8,
                  "confidence": 0.95,
                  "stability": 0.9
                }
                """);
            StringAssert.Contains(result, "\"status\":\"staged\"");
            Assert.AreEqual(0, state.Profile.Memories.Count);
        }
        Assert.AreEqual(0, state.Profile.Memories.Count);

        using (var successful = service.BeginTurn(
                   "topic-1", "line-2", "I prefer concise answers."))
        {
            await ExecuteAsync(successful, "remember_memory", """
                {
                  "title": "Concise answers",
                  "content": "The owner prefers concise answers.",
                  "category": "preference",
                  "evidence": "I prefer concise answers",
                  "importance": 0.8,
                  "confidence": 0.95,
                  "stability": 0.9
                }
                """);
            successful.Commit();
        }

        Assert.HasCount(1, state.Profile.Memories);
        Assert.AreEqual("line-2", state.Profile.Memories[0].SourceLineId);
        Assert.AreEqual(MemoryOrigins.Inferred, state.Profile.Memories[0].Origin);
        StringAssert.StartsWith(service.Notice!.Message, "Remembered:");
    }

    [TestMethod]
    public async Task Remember_RejectsUnsupportedEvidenceSecretsAndImplicitSensitiveData()
    {
        var state = new FakeMemoryState();
        var service = new MemoryService(state);

        using var turn = service.BeginTurn(
            "topic-1", "line-1", "I have depression and prefer short replies.");
        var unsupported = await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "Favorite color",
              "content": "The owner's favorite color is blue.",
              "category": "preference",
              "evidence": "favorite color is blue",
              "importance": 0.7,
              "confidence": 0.8,
              "stability": 0.8
            }
            """);
        var secret = await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "sk-proj-abcdefghijklmnop",
              "content": "The owner prefers short replies.",
              "category": "preference",
              "evidence": "prefer short replies",
              "importance": 0.7,
              "confidence": 0.8,
              "stability": 0.8
            }
            """);
        var sensitive = await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "Health",
              "content": "The owner has depression.",
              "category": "personal_fact",
              "evidence": "I have depression",
              "importance": 0.8,
              "confidence": 0.9,
              "stability": 0.9
            }
            """);

        StringAssert.StartsWith(unsupported, "ERROR:");
        StringAssert.Contains(secret, "credentials");
        StringAssert.Contains(sensitive, "explicit owner request");
        turn.Commit();
        Assert.AreEqual(0, state.Profile.Memories.Count);
    }

    [TestMethod]
    public async Task SensitiveConsent_AppliesOnlyToTheSupportedEvidence()
    {
        var state = new FakeMemoryState();
        var service = new MemoryService(state);
        using var turn = service.BeginTurn(
            "topic-1",
            "line-1",
            "Remember that I prefer short replies. I have depression.");

        var result = await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "Health",
              "content": "The owner has depression.",
              "category": "personal_fact",
              "evidence": "I have depression",
              "importance": 0.8,
              "confidence": 0.95,
              "stability": 0.9
            }
            """);
        turn.Commit();

        StringAssert.Contains(result, "explicit owner request");
        Assert.AreEqual(0, state.Profile.Memories.Count);
    }

    [TestMethod]
    public async Task ExplicitSensitiveMemory_IsAllowedAndMarkedExplicit()
    {
        var state = new FakeMemoryState();
        var service = new MemoryService(state);
        using var turn = service.BeginTurn(
            "topic-1", "line-1", "Remember that I have depression.");

        var result = await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "Health",
              "content": "The owner has depression.",
              "category": "personal_fact",
              "evidence": "I have depression",
              "importance": 0.8,
              "confidence": 0.95,
              "stability": 0.9
            }
            """);
        turn.Commit();

        StringAssert.Contains(result, "\"status\":\"staged\"");
        Assert.AreEqual(MemoryOrigins.Explicit, state.Profile.Memories.Single().Origin);
    }

    [TestMethod]
    public async Task Remember_DeduplicatesAndPreservesManualOrigin()
    {
        var state = new FakeMemoryState();
        state.Profile.Memories.Add(CreateMemory(
            "memory-1",
            "Concise answers",
            "The owner prefers concise answers with no preamble.",
            MemoryOrigins.Manual));
        var service = new MemoryService(state);
        using var turn = service.BeginTurn(
            "topic-1", "line-1", "I prefer concise answers without a preamble.");

        await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "Concise answers",
              "content": "The owner prefers concise answers without a preamble.",
              "category": "preference",
              "evidence": "I prefer concise answers without a preamble",
              "importance": 0.85,
              "confidence": 0.95,
              "stability": 0.9
            }
            """);
        turn.Commit();

        Assert.HasCount(1, state.Profile.Memories);
        var updated = state.Profile.Memories.Single();
        Assert.AreEqual("memory-1", updated.Id);
        Assert.AreEqual(2, updated.ReinforcementCount);
        Assert.AreEqual(MemoryOrigins.Manual, updated.Origin);
        Assert.IsNull(updated.SourceThreadId);
        StringAssert.StartsWith(service.Notice!.Message, "Updated memory:");
    }

    [TestMethod]
    public async Task Forget_IsStagedRequiresExplicitIntentAndCanBeUndone()
    {
        var state = new FakeMemoryState();
        state.Profile.Memories.Add(CreateMemory(
            "memory-1",
            "Concise answers",
            "The owner prefers concise answers.",
            MemoryOrigins.Explicit));
        var service = new MemoryService(state);

        using (var denied = service.BeginTurn(
                   "topic-1", "line-1", "Tell me about my concise preference."))
        {
            var deniedResult = await ExecuteAsync(
                denied, "forget_memory", """{"memory_id":"memory-1"}""");
            StringAssert.StartsWith(deniedResult, "ERROR:");
        }

        using (var allowed = service.BeginTurn(
                   "topic-1", "line-2", "Please forget my concise-answer preference."))
        {
            var result = await ExecuteAsync(
                allowed, "forget_memory", """{"memory_id":"memory-1"}""");
            StringAssert.Contains(result, "\"action\":\"deleted\"");
            Assert.HasCount(1, state.Profile.Memories);
            allowed.Commit();
        }

        Assert.AreEqual(0, state.Profile.Memories.Count);
        var notice = service.Notice;
        Assert.IsNotNull(notice);
        StringAssert.StartsWith(notice.Message, "Forgot:");
        Assert.IsTrue(service.UndoNotice(notice.Id));
        Assert.HasCount(1, state.Profile.Memories);
        Assert.IsNull(service.Notice);
    }

    [TestMethod]
    public void SuccessfulTurnTouchesSelectedMemoriesButCancelledTurnDoesNot()
    {
        var state = new FakeMemoryState();
        state.Profile.Memories.Add(CreateMemory(
            "memory-1",
            "Vegetarian meals",
            "The owner prefers vegetarian meals at conferences.",
            MemoryOrigins.Explicit));
        var service = new MemoryService(state);

        using (service.BeginTurn("topic-1", "line-1", "Plan conference meals."))
        {
        }
        Assert.AreEqual(0, state.Profile.Memories[0].RecallCount);

        using (var successful = service.BeginTurn(
                   "topic-1", "line-2", "Plan vegetarian conference meals."))
        {
            successful.Commit();
        }
        Assert.AreEqual(1, state.Profile.Memories[0].RecallCount);
        Assert.IsNotNull(state.Profile.Memories[0].LastRecalledAt);
    }

    [TestMethod]
    public async Task UndoNotice_SurfacesAConcurrentChangeInsteadOfSilentlyFailing()
    {
        var state = new FakeMemoryState();
        var service = new MemoryService(state);
        using (var turn = service.BeginTurn(
                   "topic-1", "line-1", "I prefer concise answers."))
        {
            await ExecuteAsync(turn, "remember_memory", """
                {
                  "title": "Concise answers",
                  "content": "The owner prefers concise answers.",
                  "category": "preference",
                  "evidence": "I prefer concise answers",
                  "importance": 0.8,
                  "confidence": 0.95,
                  "stability": 0.9
                }
                """);
            turn.Commit();
        }
        var notice = service.Notice!;
        state.Profile.Memories[0].Content = "A newer synchronized value.";
        state.Profile.Memories[0].UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);

        Assert.IsFalse(service.UndoNotice(notice.Id));
        Assert.IsNotNull(service.Notice);
        Assert.IsFalse(service.Notice.CanUndo);
        StringAssert.Contains(service.Notice.Message, "Could not fully undo");
    }

    [TestMethod]
    public async Task StaleTurn_DoesNotOverwriteConcurrentMemoryChange()
    {
        var state = new FakeMemoryState();
        state.Profile.Memories.Add(CreateMemory(
            "memory-1",
            "Concise answers",
            "The owner prefers concise answers.",
            MemoryOrigins.Explicit));
        var service = new MemoryService(state);
        using var turn = service.BeginTurn(
            "topic-1", "line-1", "I prefer concise answers without a preamble.");

        await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "Concise answers",
              "content": "The owner prefers concise answers without a preamble.",
              "category": "preference",
              "evidence": "I prefer concise answers without a preamble",
              "existing_memory_id": "memory-1",
              "importance": 0.85,
              "confidence": 0.95,
              "stability": 0.9
            }
            """);
        state.Profile.Memories[0].Content = "A newer change from another device.";
        state.Profile.Memories[0].UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);

        turn.Commit();

        Assert.AreEqual("A newer change from another device.", state.Profile.Memories[0].Content);
        Assert.IsNull(service.Notice);
    }

    [TestMethod]
    public async Task StaleTurn_DoesNotResurrectConcurrentDeletion()
    {
        var state = new FakeMemoryState();
        state.Profile.Memories.Add(CreateMemory(
            "memory-1",
            "Concise answers",
            "The owner prefers concise answers.",
            MemoryOrigins.Explicit));
        var service = new MemoryService(state);
        using var turn = service.BeginTurn(
            "topic-1", "line-1", "I prefer concise answers without a preamble.");

        await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "Concise answers",
              "content": "The owner prefers concise answers without a preamble.",
              "category": "preference",
              "evidence": "I prefer concise answers without a preamble",
              "existing_memory_id": "memory-1",
              "importance": 0.85,
              "confidence": 0.95,
              "stability": 0.9
            }
            """);
        state.Profile.Memories.Clear();

        turn.Commit();

        Assert.AreEqual(0, state.Profile.Memories.Count);
        Assert.IsNull(service.Notice);
    }

    [TestMethod]
    public async Task TurnCommit_IsDiscardedAfterActiveIdentityChanges()
    {
        var state = new FakeMemoryState();
        var service = new MemoryService(state);
        using var turn = service.BeginTurn(
            "topic-1", "line-1", "I prefer concise answers.");

        await ExecuteAsync(turn, "remember_memory", """
            {
              "title": "Concise answers",
              "content": "The owner prefers concise answers.",
              "category": "preference",
              "evidence": "I prefer concise answers",
              "importance": 0.8,
              "confidence": 0.95,
              "stability": 0.9
            }
            """);
        state.ActiveAccountId = "account-2";

        turn.Commit();

        Assert.AreEqual(0, state.Profile.Memories.Count);
        Assert.IsNull(service.Notice);
    }

    private static async Task<string> ExecuteAsync(
        MemoryService.MemoryTurnSession turn,
        string toolName,
        string arguments)
    {
        var tool = turn.Tools.Single(candidate => candidate.Name == toolName);
        using var document = JsonDocument.Parse(arguments);
        return await tool.ExecuteAsync(document.RootElement);
    }

    private static MemoryItem CreateMemory(
        string id,
        string title,
        string content,
        string origin)
    {
        var now = DateTimeOffset.UtcNow;
        return new MemoryItem
        {
            Id = id,
            Title = title,
            Content = content,
            Category = MemoryCategories.Preference,
            Origin = origin,
            Importance = 0.8,
            Confidence = 0.95,
            Stability = 0.9,
            ReinforcementCount = 1,
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-1),
            LastReinforcedAt = now.AddDays(-1)
        };
    }

    private sealed class FakeMemoryState : IMemoryState
    {
        public MeshProfile Profile { get; } = new();
        public string? ActiveAccountId { get; set; } = "account-1";

        public MemorySnapshot SnapshotMemories()
            => new(
                ActiveAccountId,
                Profile.Memories.Select(MemoryPolicy.Clone).ToList());

        public bool UpsertMemory(
            string? accountId,
            MemoryItem memory,
            MemoryItem? expected,
            out MemoryItem? previous)
        {
            var normalized = MemoryPolicy.Normalize(memory);
            var existing = Profile.Memories.FirstOrDefault(item => item.Id == normalized.Id);
            previous = existing is null ? null : MemoryPolicy.Clone(existing);
            if (!string.Equals(accountId, ActiveAccountId, StringComparison.Ordinal)
                || (expected is null
                    ? existing is not null
                    : existing is null || !MemoryPolicy.SharedEquals(existing, expected))
                || existing is not null && MemoryPolicy.SharedEquals(existing, normalized))
                return false;
            if (existing is null)
                Profile.Memories.Add(MemoryPolicy.Clone(normalized));
            else
                MemoryPolicy.CopyShared(normalized, existing);
            return true;
        }

        public bool DeleteMemory(
            string? accountId,
            string id,
            MemoryItem expected,
            out MemoryItem? previous)
        {
            var existing = Profile.Memories.FirstOrDefault(item => item.Id == id);
            previous = existing is null ? null : MemoryPolicy.Clone(existing);
            return string.Equals(accountId, ActiveAccountId, StringComparison.Ordinal)
                   && existing is not null
                   && MemoryPolicy.SharedEquals(existing, expected)
                   && Profile.Memories.Remove(existing);
        }

        public void TouchMemories(
            string? accountId,
            IEnumerable<string> ids,
            DateTimeOffset? recalledAt = null)
        {
            if (!string.Equals(accountId, ActiveAccountId, StringComparison.Ordinal)) return;
            var selected = ids.ToHashSet(StringComparer.Ordinal);
            var at = recalledAt ?? DateTimeOffset.UtcNow;
            foreach (var memory in Profile.Memories.Where(item => selected.Contains(item.Id)))
            {
                memory.RecallCount++;
                memory.LastRecalledAt = at;
            }
        }
    }
}
