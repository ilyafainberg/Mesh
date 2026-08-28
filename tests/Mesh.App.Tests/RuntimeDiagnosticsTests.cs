using Mesh.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class RuntimeDiagnosticsTests
{
    private string directory = "";

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), "mesh-runtime-diagnostics-tests", Guid.NewGuid().ToString("n"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public void ActivePreviousSessionIsReportedAsUnexpected()
    {
        var first = new RuntimeDiagnostics(directory);
        first.StartSession("ios", "1.0.0", detectUnexpectedTermination: true);
        first.MarkLifecycle("active");

        var second = new RuntimeDiagnostics(directory);
        second.StartSession("ios", "1.0.1", detectUnexpectedTermination: true);

        Assert.IsTrue(second.PreviousSessionEndedUnexpectedly);
        Assert.AreEqual("active", second.PreviousSessionPhase);
        StringAssert.Contains(second.CreateReport(), "Previous session ended unexpectedly");
    }

    [TestMethod]
    public void BackgroundPreviousSessionIsNotReportedAsCrash()
    {
        var first = new RuntimeDiagnostics(directory);
        first.StartSession("ios", "1.0.0", detectUnexpectedTermination: true);
        first.MarkLifecycle("background");

        var second = new RuntimeDiagnostics(directory);
        second.StartSession("ios", "1.0.1", detectUnexpectedTermination: true);

        Assert.IsFalse(second.PreviousSessionEndedUnexpectedly);
        Assert.IsNull(second.PreviousSessionPhase);
    }

    [TestMethod]
    public void NativeDiagnosticPayloadsAreDeduplicated()
    {
        var diagnostics = new RuntimeDiagnostics(directory);
        diagnostics.StartSession("ios", "1.0.0", detectUnexpectedTermination: true);

        diagnostics.RecordDiagnosticPayload("metrickit", "{\"kind\":\"crash\"}");
        diagnostics.RecordDiagnosticPayload("metrickit", "{\"kind\":\"crash\"}");

        var report = diagnostics.CreateReport();
        Assert.AreEqual(1, Count(report, "{\"kind\":\"crash\"}"));
    }

    [TestMethod]
    public void ClearRemovesLogsButPreservesCurrentSessionMarker()
    {
        var diagnostics = new RuntimeDiagnostics(directory);
        diagnostics.StartSession("ios", "1.0.0", detectUnexpectedTermination: true);
        diagnostics.RecordEvent("test", "remove me");

        diagnostics.Clear();

        Assert.IsFalse(diagnostics.HasEntries);
        Assert.IsFalse(diagnostics.CreateReport().Contains("remove me", StringComparison.Ordinal));

        var next = new RuntimeDiagnostics(directory);
        next.StartSession("ios", "1.0.1", detectUnexpectedTermination: true);
        Assert.IsTrue(next.PreviousSessionEndedUnexpectedly);
    }

    [TestMethod]
    public void CorruptSessionMarkerIsQuarantinedAndReplaced()
    {
        Directory.CreateDirectory(directory);
        var markerPath = Path.Combine(directory, "session.json");
        File.WriteAllText(markerPath, "{");

        var diagnostics = new RuntimeDiagnostics(directory);
        diagnostics.StartSession("ios", "1.0.0", detectUnexpectedTermination: true);

        Assert.IsFalse(diagnostics.PreviousSessionEndedUnexpectedly);
        Assert.IsTrue(File.Exists(markerPath));
        Assert.IsTrue(File.Exists(markerPath + ".corrupt"));
        StringAssert.Contains(File.ReadAllText(markerPath), "launching");
        StringAssert.Contains(diagnostics.CreateReport(), "session-start");
    }

    [TestMethod]
    public void UiLoggerPersistsRendererExceptionsButIgnoresUnrelatedWarnings()
    {
        var diagnostics = new RuntimeDiagnostics(directory);
        diagnostics.StartSession("windows", "1.0.0");
        using var provider = new RuntimeDiagnosticsLoggerProvider(diagnostics);

        var renderer = provider.CreateLogger("Microsoft.AspNetCore.Components.RenderTree.Renderer");
        renderer.LogError(
            new InvalidOperationException("render failed"),
            "Unhandled exception rendering component {ComponentId}",
            42);
        provider.CreateLogger("System.Net.Http.HttpClient").LogWarning("background warning");

        var report = diagnostics.CreateReport();
        StringAssert.Contains(report, "Unhandled exception rendering component 42");
        StringAssert.Contains(report, "InvalidOperationException: render failed");
        Assert.IsFalse(report.Contains("background warning", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WidgetDiagnosticsPersistNormalizedStageMetadata()
    {
        var diagnostics = new RuntimeDiagnostics(directory);
        diagnostics.StartSession("ios", "1.0.0", detectUnexpectedTermination: true);
        var bridge = new WidgetDiagnosticsBridge(diagnostics);

        bridge.RecordStage(" First Paint!! ", "mode=host\r\npurpose=self-test");
        bridge.RecordStage("!!!", "ignored");

        var report = diagnostics.CreateReport();
        StringAssert.Contains(report, "[widget-render] stage=first-paint; mode=host purpose=self-test");
        Assert.IsFalse(report.Contains("ignored", StringComparison.Ordinal));
    }

    private static int Count(string value, string target)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(target, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += target.Length;
        }
        return count;
    }
}
