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
