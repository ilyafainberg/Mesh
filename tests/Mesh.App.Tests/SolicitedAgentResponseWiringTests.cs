using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class SolicitedAgentResponseWiringTests
{
    [TestMethod]
    public void AgentResponse_ReturnsBefore_UnallowedRequestStaging()
    {
        var source = ReadSource("src", "Mesh.App", "Services", "MeshClient.cs");
        var responseGuard = source.IndexOf(
            "env.Kind is MeshKinds.AgentResponse or MeshKinds.AtomicAgentResponse",
            StringComparison.Ordinal);
        var requestStaging = source.IndexOf("if (!allowed)", responseGuard, StringComparison.Ordinal);

        Assert.IsTrue(responseGuard >= 0);
        Assert.IsTrue(requestStaging > responseGuard);
        StringAssert.Contains(source[responseGuard..requestStaging], "return;");
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
