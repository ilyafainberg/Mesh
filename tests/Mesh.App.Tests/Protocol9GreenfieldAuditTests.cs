using System.Reflection;
using Mesh.App.Services;
using Mesh.Relay.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class Protocol9GreenfieldAuditTests
{
    [TestMethod]
    public void AppAndRelayTypeNames_DoNotUseRetiredProtocolNames()
    {
        var blocked = new[]
        {
            "Protocol " + "7",
            "Protocol" + "7",
            "Protocol " + "8",
            "Protocol" + "8",
            "Mesh" + "117",
            "Device" + "Sync",
            "Background" + "Sync",
            "Durable" + "Topic",
            "Durable" + "Relay",
            "Agent" + "Dispatch",
            "Queue" + "Ack",
            "Queue" + "Drain",
            "Queue" + "Enqueue",
            "in" + "box"
        };
        var assemblies = new[]
        {
            typeof(MeshDb).Assembly,
            typeof(CosmosRelayStore).Assembly
        };

        var offenders = assemblies
            .SelectMany(SafeTypes)
            .Select(type => type.FullName ?? type.Name)
            .Where(name => blocked.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.HasCount(0, offenders, string.Join(", ", offenders));
    }

    [TestMethod]
    public void RelayCosmosStore_ProvisionsOnlyMetadataContainers()
    {
        CollectionAssert.AreEquivalent(
            new[] { "handles", "rate-policies", "invites", "services" },
            CosmosRelayStore.ProvisionedContainers.ToArray());
        foreach (var name in CosmosRelayStore.ProvisionedContainers)
            Assert.IsFalse(CosmosRelayStore.ForbiddenContainerNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MeshDbSchema_HasNoRetiredReplicationTables()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "p9-audit", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var dbPath = Path.Combine(directory, "audit.meshdb");
            var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
            using var db = MeshDb.Open(dbPath, key);
            using var command = db.RawConnectionForTest.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));

            var blocked = new[]
            {
                "device" + "_sync",
                "snap" + "shot",
                "relay_" + "in" + "box",
                "relay_" + "queue",
                "agent_" + "dispatch"
            };
            var offenders = names
                .Where(name => blocked.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.HasCount(0, offenders, string.Join(", ", offenders));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type is not null)!; }
    }
}
