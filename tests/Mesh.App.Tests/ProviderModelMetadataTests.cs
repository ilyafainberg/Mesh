using System.Text.Json;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ProviderModelMetadataTests
{
    [TestMethod]
    public void ReadOpenAi_ReturnsProviderReportedModel()
    {
        using var response = JsonDocument.Parse("{\"model\":\" deepseek/deepseek-chat-v3 \"}");

        var model = ProviderModelMetadata.ReadOpenAi(response.RootElement);

        Assert.AreEqual("deepseek/deepseek-chat-v3", model);
    }

    [TestMethod]
    public void ReadOpenAi_ReturnsNullWhenModelIsMissingOrInvalid()
    {
        using var missing = JsonDocument.Parse("{\"choices\":[]}");
        using var blank = JsonDocument.Parse("{\"model\":\"   \"}");
        using var wrongType = JsonDocument.Parse("{\"model\":42}");

        Assert.IsNull(ProviderModelMetadata.ReadOpenAi(missing.RootElement));
        Assert.IsNull(ProviderModelMetadata.ReadOpenAi(blank.RootElement));
        Assert.IsNull(ProviderModelMetadata.ReadOpenAi(wrongType.RootElement));
    }
}
