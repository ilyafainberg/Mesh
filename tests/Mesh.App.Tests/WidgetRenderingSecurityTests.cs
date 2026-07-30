using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class WidgetRenderingSecurityTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    public void WidgetFrames_KeepOpaqueScriptOnlySandbox()
    {
        var sources = new[]
        {
            Read("src", "Mesh.App", "Components", "MessageContent.razor"),
            Read("src", "Mesh.App", "Components", "Pages", "Widgets.razor")
        };

        foreach (var source in sources)
        {
            var values = Regex.Matches(source, "sandbox=\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value)
                .ToArray();
            Assert.IsTrue(values.Length > 0);
            Assert.IsTrue(values.All(value => value == "allow-scripts"));
            Assert.IsFalse(source.Contains("allow-same-origin", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("allow-top-navigation", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("allow-popups", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("allow-forms", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void WidgetPolicy_BlocksNetworkEmbeddingAndForms()
    {
        var script = Script();
        StringAssert.Contains(script, "default-src 'none'");
        StringAssert.Contains(script, "connect-src 'none'");
        StringAssert.Contains(script, "frame-src 'none'");
        StringAssert.Contains(script, "object-src 'none'");
        StringAssert.Contains(script, "form-action 'none'");
        StringAssert.Contains(script, "base-uri 'none'");
    }

    [TestMethod]
    public void WidgetPolicy_IsAlwaysParsedBeforeUntrustedMarkup()
    {
        var script = Script();
        StringAssert.Contains(script,
            "return '<!doctype html><html><head>' + policy + bootstrap(nonce) +");
        StringAssert.Contains(script, "'</head><body>' + source + '</body></html>'");
        Assert.IsFalse(script.Contains("source.replace(head", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("source.replace(root", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IOSRenderer_PrefersInlineDocumentsAndRetainsLoadedFrame()
    {
        var script = Script();
        StringAssert.Contains(script, "ios ? ['srcdoc', 'data', 'blob', 'host']");
        StringAssert.Contains(script, ": ['srcdoc']");
        StringAssert.Contains(script, "acceptLoadedFrame(iframe, state)");
        StringAssert.Contains(script, "iframe.dataset.widgetReady = 'unconfirmed'");
        StringAssert.Contains(script, "startup confirmation was not received");
    }

    [TestMethod]
    public void WidgetMessages_AreBoundToOpaqueOriginSourceAndNonce()
    {
        var script = Script();
        StringAssert.Contains(script, "event.origin !== 'null'");
        StringAssert.Contains(script, "frame.contentWindow !== event.source");
        StringAssert.Contains(script, "frame.dataset.widgetNonce !== data.nonce");
    }

    [TestMethod]
    public void NativeHosts_BlockExternalSubframeNavigation()
    {
        var page = Read("src", "Mesh.App", "MainPage.xaml.cs");
        StringAssert.Contains(page, "var blocked = fromSubframe");
        StringAssert.Contains(page, "WKNavigationActionPolicy.Cancel");
        StringAssert.Contains(page, "scheme is \"about\" or \"data\" or \"blob\"");
        StringAssert.Contains(page, "url?.Path, \"/widget-host.html\"");
        StringAssert.Contains(page, "IsSameDocumentFragmentNavigation");
        StringAssert.Contains(page, "requestedValue.IndexOf('#') < 0");
        StringAssert.Contains(page, "inner.DecidePolicy(webView, navigationAction, decisionHandler)");
        StringAssert.Contains(page, "request is { IsForMainFrame: false }");
        StringAssert.Contains(page, "widgetWebView2.FrameNavigationStarting += OnFrameNavigationStarting");
        StringAssert.Contains(page, "about:srcdoc");
    }

    [TestMethod]
    public void WidgetFailures_AreVisibleWithoutLoggingGeneratedHtml()
    {
        var script = Script();
        var messageContent = Read("src", "Mesh.App", "Components", "MessageContent.razor");
        var widgets = Read("src", "Mesh.App", "Components", "Pages", "Widgets.razor");
        StringAssert.Contains(messageContent, "Loading secure widget...");
        StringAssert.Contains(widgets, "Loading secure widget...");
        StringAssert.Contains(script, "Widget could not load securely.");
        Assert.IsFalse(script.Contains("console.error('Widget failed:', message", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("console.error('sandboxFrame.setFrameHtml failed', e)", StringComparison.Ordinal));
    }

    private static string Script()
        => Read("src", "Mesh.App", "wwwroot", "js", "sandboxframe.js");

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot }.Concat(parts).ToArray()));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mesh.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Mesh repository root.");
    }
}
