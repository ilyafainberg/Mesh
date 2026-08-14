using System.Text;
using System.Text.Json.Nodes;
using Mesh.App.Domain;
using Mesh.App.Services;
using Mesh.BuiltIns.Compiler;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class BuiltInContentTests
{
    [TestMethod]
    public void Compiler_GeneratesDeterministicCatalog()
    {
        var root = CreateValidPack();
        try
        {
            var indexPath = Path.Combine(root, "builtins.index.json");
            var first = BuiltInContentCompiler.Compile(root, indexPath);
            var firstJson = File.ReadAllText(indexPath);
            var second = BuiltInContentCompiler.Compile(root, indexPath);

            Assert.AreEqual(first.CatalogHash, second.CatalogHash);
            Assert.AreEqual(first.ContentVersion, second.ContentVersion);
            Assert.AreEqual(firstJson, File.ReadAllText(indexPath));
            Assert.AreEqual(6, first.Items.Count);
            Assert.IsTrue(first.Items.All(item => !item.Path.Contains('\\')));
            var bytes = File.ReadAllBytes(indexPath);
            Assert.AreEqual((byte)'\n', bytes[^1]);
            Assert.AreNotEqual((byte)'\r', bytes[^2]);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Compiler_NormalizesLineEndingsForCatalogHash()
    {
        var lfRoot = CreateValidPack();
        var crlfRoot = CreateValidPack();
        try
        {
            foreach (var path in Directory.EnumerateFiles(crlfRoot, "*.md", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
                File.WriteAllText(path, text.Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));
            }

            var lf = BuiltInContentCompiler.Compile(lfRoot, Path.Combine(lfRoot, "builtins.index.json"));
            var crlf = BuiltInContentCompiler.Compile(crlfRoot, Path.Combine(crlfRoot, "builtins.index.json"));

            Assert.AreEqual(lf.CatalogHash, crlf.CatalogHash);
            CollectionAssert.AreEqual(
                lf.Items.Select(item => item.Sha256).ToArray(),
                crlf.Items.Select(item => item.Sha256).ToArray());
        }
        finally
        {
            DeleteDirectory(lfRoot);
            DeleteDirectory(crlfRoot);
        }
    }
    [TestMethod]
    public void Compiler_InvalidMetadataFailsBuild()
    {
        var root = CreateValidPack();
        try
        {
            var path = Path.Combine(root, "Knowledge", "relay.md");
            File.WriteAllText(path, File.ReadAllText(path).Replace("roles: owner,guest", "roles: owner,admin"));

            var exception = Assert.ThrowsException<BuiltInCompilationException>(() =>
                BuiltInContentCompiler.Compile(root, Path.Combine(root, "builtins.index.json")));

            StringAssert.Contains(exception.Message, "invalid role 'admin'");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Compiler_RejectsExecutableContentInMetadata()
    {
        var root = CreateValidPack();
        try
        {
            var path = Path.Combine(root, "Knowledge", "relay.md");
            File.WriteAllText(path, File.ReadAllText(path).Replace(
                "How live Relay connectivity affects delivery.",
                "<script>alert('unexpected')</script>"));

            var exception = Assert.ThrowsException<BuiltInCompilationException>(() =>
                BuiltInContentCompiler.Compile(root, Path.Combine(root, "builtins.index.json")));

            StringAssert.Contains(exception.Message, "executable script or HTML content is prohibited");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Compiler_RejectsChangedIdAtSamePath()
    {
        var root = CreateValidPack();
        try
        {
            var indexPath = Path.Combine(root, "builtins.index.json");
            BuiltInContentCompiler.Compile(root, indexPath);
            var path = Path.Combine(root, "Knowledge", "relay.md");
            File.WriteAllText(path, File.ReadAllText(path).Replace(
                "builtin:knowledge:relay", "builtin:knowledge:relay-v2"));

            var exception = Assert.ThrowsException<BuiltInCompilationException>(() =>
                BuiltInContentCompiler.Compile(root, indexPath));

            StringAssert.Contains(exception.Message, "id is immutable");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Provider_LoadsRoleFilteredSummariesAndBodies()
    {
        var root = CreateValidPack();
        try
        {
            BuiltInContentCompiler.Compile(root, Path.Combine(root, "builtins.index.json"));
            var diagnostics = new List<string>();
            var provider = CreateProvider(root, diagnostics);

            CollectionAssert.AreEqual(
                new[] { "builtin:policy:core", "builtin:policy:owner" },
                provider.GetPolicies(AgentRole.Owner).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(
                new[] { "builtin:policy:core", "builtin:policy:service" },
                provider.GetPolicies(AgentRole.Service).Select(item => item.Id).ToArray());
            Assert.AreEqual(1, provider.GetKnowledge(AgentRole.Guest).Count);
            Assert.AreEqual(0, provider.GetKnowledge(AgentRole.Service).Count);
            Assert.AreEqual(1, provider.GetSkills(AgentRole.Owner).Count);
            Assert.AreEqual(0, provider.GetSkills(AgentRole.Guest).Count);

            var summary = provider.GetKnowledge(AgentRole.Owner).Single();
            StringAssert.Contains(summary.Content, "Keywords: relay, connection, network");
            var body = provider.LoadKnowledge(summary.Id)!;
            StringAssert.Contains(body.Content, "Relay forwarding depends on live connectivity.");
            Assert.IsFalse(body.Content.Contains("---", StringComparison.Ordinal));
            Assert.IsTrue(diagnostics.Single().Contains("policies=4", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Provider_HashMismatchFailsClosed()
    {
        var root = CreateValidPack();
        try
        {
            BuiltInContentCompiler.Compile(root, Path.Combine(root, "builtins.index.json"));
            File.AppendAllText(Path.Combine(root, "Knowledge", "relay.md"), "\nchanged");
            var provider = CreateProvider(root, new List<string>());

            Assert.ThrowsException<BuiltInContentException>(() => provider.GetPolicies(AgentRole.Owner));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Provider_MissingOptionalItemIsExcludedAndDiagnosed()
    {
        var root = CreateValidPack();
        try
        {
            BuiltInContentCompiler.Compile(root, Path.Combine(root, "builtins.index.json"));
            File.Delete(Path.Combine(root, "Knowledge", "relay.md"));
            var diagnostics = new List<string>();
            var provider = CreateProvider(root, diagnostics);

            Assert.AreEqual(2, provider.GetPolicies(AgentRole.Owner).Count);
            Assert.AreEqual(0, provider.GetKnowledge(AgentRole.Owner).Count);
            Assert.AreEqual(1, provider.Diagnostics.LoadFailures.Count);
            Assert.IsTrue(diagnostics.Any(message => message.Contains("reason=missing", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Provider_LoadsCrLfContentWhoseCanonicalFormFitsTheLimit()
    {
        var root = CreateValidPack();
        try
        {
            var path = Path.Combine(root, "Knowledge", "relay.md");
            var header = """
                ---
                id: builtin:knowledge:relay
                type: knowledge
                title: Relay connectivity
                description: How live Relay connectivity affects delivery.
                roles: owner,guest
                keywords: relay,connection,network
                ---

                # Relay connectivity

                """.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);
            var body = string.Concat(Enumerable.Repeat("x\r\n", 25_000));
            File.WriteAllText(path, header + body, new UTF8Encoding(false));
            BuiltInContentCompiler.Compile(root, Path.Combine(root, "builtins.index.json"));

            var provider = CreateProvider(root, new List<string>());

            Assert.AreEqual(25_000, provider.LoadKnowledge("builtin:knowledge:relay")!.Content.Count(character => character == 'x'));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Provider_MalformedCatalogFailsClosed()
    {
        var root = CreateValidPack();
        try
        {
            var indexPath = Path.Combine(root, "builtins.index.json");
            BuiltInContentCompiler.Compile(root, indexPath);
            var index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
            index["items"]!.AsArray()[0]!["roles"] = null;
            File.WriteAllText(indexPath, index.ToJsonString());
            var provider = CreateProvider(root, new List<string>());

            Assert.ThrowsException<BuiltInContentException>(() => provider.GetPolicies(AgentRole.Owner));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task Selector_UsesOneBudgetAndExcludesIrrelevantBuiltIns()
    {
        var root = CreateValidPack();
        try
        {
            BuiltInContentCompiler.Compile(root, Path.Combine(root, "builtins.index.json"));
            var provider = CreateProvider(root, new List<string>());

            var irrelevant = await AgentPromptContentSelector.SelectAsync(
                provider, AgentRole.Owner, Array.Empty<KnowledgeItem>(), Array.Empty<Skill>(),
                (_, _) => Task.FromResult<KnowledgeItem?>(null),
                (_, _) => Task.FromResult<Skill?>(null),
                "weather forecast", TokenOptimizationLevel.Balanced, compact: false,
                CancellationToken.None);
            Assert.AreEqual(0, irrelevant.BuiltInKnowledge.Count);
            Assert.AreEqual(0, irrelevant.BuiltInSkills.Count);

            var relevant = await AgentPromptContentSelector.SelectAsync(
                provider, AgentRole.Owner, Array.Empty<KnowledgeItem>(), Array.Empty<Skill>(),
                (_, _) => Task.FromResult<KnowledgeItem?>(null),
                (_, _) => Task.FromResult<Skill?>(null),
                "relay connection and missing sync messages", TokenOptimizationLevel.Balanced, compact: false,
                CancellationToken.None);
            Assert.AreEqual("builtin:knowledge:relay", relevant.BuiltInKnowledge.Single().Id);
            Assert.AreEqual("builtin:skill:sync", relevant.BuiltInSkills.Single().Id);

            var candidates = Enumerable.Range(0, 12)
                .Select(index => new AgentContentSummary(
                    index.ToString(), AgentContentKind.Knowledge, "relay", "relay connection",
                    1_000, index, IsBuiltIn: index % 2 == 0))
                .ToArray();
            var selection = TokenOptimizer.SelectAgentContent(
                candidates, "relay", TokenOptimizationLevel.Balanced, compact: false);
            Assert.AreEqual(8, selection.Included.Count);
            Assert.IsTrue(selection.Included.Sum(item => item.BodyBytes) <=
                          TokenOptimizer.AgentContentByteBudget(TokenOptimizationLevel.Balanced, compact: false));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }


    [TestMethod]
    public async Task Selector_LoadsOnlyRelevantUserBodies()
    {
        var root = CreateValidPack();
        try
        {
            BuiltInContentCompiler.Compile(root, Path.Combine(root, "builtins.index.json"));
            var provider = CreateProvider(root, new List<string>());
            var requested = new List<string>();
            var summaries = new[]
            {
                new KnowledgeItem
                {
                    Id = "deploy",
                    Title = "Deployment runbook",
                    ContentByteCount = 64,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new KnowledgeItem
                {
                    Id = "cooking",
                    Title = "Cooking notes",
                    ContentByteCount = 64,
                    UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                }
            };

            var selected = await AgentPromptContentSelector.SelectAsync(
                provider,
                AgentRole.Owner,
                summaries,
                Array.Empty<Skill>(),
                (id, _) =>
                {
                    requested.Add(id);
                    return Task.FromResult<KnowledgeItem?>(new KnowledgeItem
                    {
                        Id = id,
                        Title = summaries.Single(item => item.Id == id).Title,
                        Content = id == "deploy" ? "Deployment validation and rollback steps." : "Recipes."
                    });
                },
                (_, _) => Task.FromResult<Skill?>(null),
                "validate deployment rollback",
                TokenOptimizationLevel.Balanced,
                compact: false,
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "deploy" }, requested);
            Assert.AreEqual("deploy", selected.UserKnowledge.Single().Id);
            Assert.IsTrue(selected.OmittedUserKnowledgeNames.Contains("Cooking notes"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }
    private static BuiltInContentProvider CreateProvider(string root, List<string> diagnostics)

        => new(path =>
        {
            if (!path.StartsWith("BuiltIns/", StringComparison.Ordinal))
                throw new FileNotFoundException(path);
            var relative = path["BuiltIns/".Length..].Replace('/', Path.DirectorySeparatorChar);
            Stream stream = File.OpenRead(Path.Combine(root, relative));
            return Task.FromResult(stream);
        }, diagnostics.Add);

    private static string CreateValidPack()
    {
        var root = Path.Combine(Path.GetTempPath(), "mesh-builtins-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "Policies"));
        Directory.CreateDirectory(Path.Combine(root, "Knowledge"));
        Directory.CreateDirectory(Path.Combine(root, "Skills", "sync"));

        Write(root, "Policies/core.md", Policy("core", "Core", "owner,guest,service", 100));
        Write(root, "Policies/owner.md", Policy("owner", "Owner", "owner", 90));
        Write(root, "Policies/guest.md", Policy("guest", "Guest", "guest", 90));
        Write(root, "Policies/service.md", Policy("service", "Service", "service", 90));
        Write(root, "Knowledge/relay.md", """
            ---
            id: builtin:knowledge:relay
            type: knowledge
            title: Relay connectivity
            description: How live Relay connectivity affects delivery.
            roles: owner,guest
            keywords: relay,connection,network
            ---

            # Relay connectivity

            Relay forwarding depends on live connectivity.
            """);
        Write(root, "Skills/sync/SKILL.md", """
            ---
            id: builtin:skill:sync
            type: skill
            name: Troubleshoot synchronization
            description: Diagnose missing cross-device state.
            roles: owner
            triggers: sync,missing messages,replication
            ---

            # Instructions

            1. Identify the devices.
            2. Verify connectivity and authorization.
            """);
        return root;
    }

    private static string Policy(string id, string title, string roles, int priority) => $"""
        ---
        id: builtin:policy:{id}
        type: policy
        title: {title}
        roles: {roles}
        priority: {priority}
        ---

        - Follow the {id} policy.
        """;

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Trim() + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
