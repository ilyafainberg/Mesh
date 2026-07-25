using System.Text.Json;
using Mesh.App.Domain;
using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class AgentToolExecutorTests
{
    [TestMethod]
    public async Task InternalTool_ExecutesWithoutUserVisibleProgress()
    {
        var progress = new RecordingProgress();
        var tool = new TestTool("remember_memory", isInternal: true, _ => "staged");

        var result = await AgentToolExecutor.ExecuteAsync(
            [tool], tool.Name, "{}", CancellationToken.None, progress);

        Assert.AreEqual("staged", result);
        Assert.AreEqual(0, progress.Steps.Count);
    }

    [TestMethod]
    public async Task VisibleTool_ReportsStartedAndDone()
    {
        var progress = new RecordingProgress();
        var tool = new TestTool("web_search", isInternal: false, _ => "result");

        var result = await AgentToolExecutor.ExecuteAsync(
            [tool], tool.Name, "{\"query\":\"mesh\"}", CancellationToken.None, progress);

        Assert.AreEqual("result", result);
        Assert.HasCount(2, progress.Steps);
        Assert.AreEqual(AgentStepState.Started, progress.Steps[0].State);
        Assert.AreEqual(AgentStepState.Done, progress.Steps[1].State);
    }

    [TestMethod]
    public async Task InternalToolFailure_RemainsHiddenAndReturnsErrorToModel()
    {
        var progress = new RecordingProgress();
        var tool = new TestTool(
            "forget_memory",
            isInternal: true,
            _ => throw new InvalidOperationException("failed"));

        var result = await AgentToolExecutor.ExecuteAsync(
            [tool], tool.Name, "{}", CancellationToken.None, progress);

        Assert.AreEqual("ERROR: failed", result);
        Assert.AreEqual(0, progress.Steps.Count);
    }

    [TestMethod]
    public async Task Cancellation_PropagatesInsteadOfBecomingToolError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var tool = new TestTool(
            "remember_memory",
            isInternal: true,
            _ => throw new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            AgentToolExecutor.ExecuteAsync(
                [tool], tool.Name, "{}", cancellation.Token));
    }

    private sealed class RecordingProgress : IProgress<AgentStep>
    {
        public List<AgentStep> Steps { get; } = new();
        public void Report(AgentStep value) => Steps.Add(value);
    }

    private sealed class TestTool(
        string name,
        bool isInternal,
        Func<JsonElement, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public object ParametersSchema => new { type = "object" };
        public bool IsInternal => isInternal;
        public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
            => Task.FromResult(execute(args));
    }
}
