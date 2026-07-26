using Mesh.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace Mesh.App.Tests;

[TestClass]
public sealed class DeviceSyncProfileProtocolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void ProfileOperationKinds_HaveStableValues()
    {
        Assert.AreEqual("contact.upsert", DeviceSyncKinds.ContactUpsert);
        Assert.AreEqual("contact.delete", DeviceSyncKinds.ContactDelete);
        Assert.AreEqual("circle.upsert", DeviceSyncKinds.CircleUpsert);
        Assert.AreEqual("circle.delete", DeviceSyncKinds.CircleDelete);
        Assert.AreEqual("memory.upsert", DeviceSyncKinds.MemoryUpsert);
        Assert.AreEqual("memory.delete", DeviceSyncKinds.MemoryDelete);
    }

    [TestMethod]
    public void ContactPayload_RoundTripsApprovedProfileFields()
    {
        var contact = new DeviceSyncContact(
            "alice",
            "Alice",
            ["friends", "work"],
            true,
            ["signing-key-1", "signing-key-2"],
            true,
            true,
            false);

        var json = JsonSerializer.Serialize(contact, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DeviceSyncContact>(json, JsonOptions);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(contact.Handle, roundTrip.Handle);
        Assert.AreEqual(contact.DisplayName, roundTrip.DisplayName);
        CollectionAssert.AreEqual(contact.Circles.ToArray(), roundTrip.Circles.ToArray());
        Assert.AreEqual(contact.Allowed, roundTrip.Allowed);
        CollectionAssert.AreEqual(contact.SigningKeys.ToArray(), roundTrip.SigningKeys.ToArray());
        Assert.AreEqual(contact.KeyChanged, roundTrip.KeyChanged);
        Assert.AreEqual(contact.Muted, roundTrip.Muted);
        Assert.AreEqual(contact.Blocked, roundTrip.Blocked);
        Assert.IsNull(typeof(DeviceSyncContact).GetProperty("TokensSpent"));

        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.TryGetProperty("tokensSpent", out _));
    }

    [TestMethod]
    public void MemoryPayload_RoundTripsSharedFieldsOnly()
    {
        var created = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var memory = new DeviceSyncMemory(
            "memory-1",
            "Concise answers",
            "The owner prefers concise answers.",
            "preference",
            "explicit",
            0.8,
            0.95,
            0.9,
            3,
            "topic-1",
            "line-1",
            created,
            created.AddDays(1),
            created.AddDays(1));

        var json = JsonSerializer.Serialize(memory, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DeviceSyncMemory>(json, JsonOptions);

        Assert.AreEqual(memory, roundTrip);
        Assert.IsNull(typeof(DeviceSyncMemory).GetProperty("RecallCount"));
        Assert.IsNull(typeof(DeviceSyncMemory).GetProperty("LastRecalledAt"));
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.TryGetProperty("recallCount", out _));
        Assert.IsFalse(document.RootElement.TryGetProperty("lastRecalledAt", out _));
    }

    [TestMethod]
    public void LinePayload_RoundTripsProviderModelAndReadsOlderPayload()
    {
        var line = new DeviceSyncLine(
            "line-1", "assistant", "answer", "agent", "sent",
            DateTimeOffset.UtcNow, null, false, null, "prompt-1",
            "deepseek/deepseek-chat");

        var json = JsonSerializer.Serialize(line, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DeviceSyncLine>(json, JsonOptions);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual("deepseek/deepseek-chat", roundTrip.ModelId);

        var olderJson = "{\"id\":\"line-1\",\"role\":\"assistant\",\"text\":\"answer\",\"via\":\"agent\",\"status\":\"sent\",\"at\":\"2026-07-25T10:00:00+00:00\",\"senderHandle\":null,\"internal\":false,\"reasoning\":null,\"replyToLineId\":null}";
        var older = JsonSerializer.Deserialize<DeviceSyncLine>(olderJson, JsonOptions);

        Assert.IsNotNull(older);
        Assert.IsNull(older.ModelId);
    }

    [TestMethod]
    public void CirclePayload_RoundTripsApprovalRequirement()
    {
        var circle = new DeviceSyncCircle(
            "trusted",
            true,
            [new DeviceSyncCircleRename(
                "friends",
                DeviceSyncVersion.Create(DateTimeOffset.UtcNow, "device", "rename"))]);

        var json = JsonSerializer.Serialize(circle, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DeviceSyncCircle>(json, JsonOptions);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(circle.Name, roundTrip.Name);
        Assert.AreEqual(circle.RequireApproval, roundTrip.RequireApproval);
        Assert.IsNotNull(roundTrip.Renames);
        Assert.HasCount(1, roundTrip.Renames);
        Assert.AreEqual(circle.Renames![0], roundTrip.Renames[0]);
    }
}
