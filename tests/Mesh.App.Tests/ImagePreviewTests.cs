using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class ImagePreviewTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    public void Overlay_HasDialogKeyboardAndBackdropSemantics()
    {
        var preview = Read("src", "Mesh.App", "Components", "Shared", "ImagePreview.razor");

        StringAssert.Contains(preview, "role=\"dialog\"");
        StringAssert.Contains(preview, "aria-modal=\"true\"");
        StringAssert.Contains(preview, "aria-label=\"Close image preview\"");
        StringAssert.Contains(preview, "args.Key == \"Escape\"");
        StringAssert.Contains(preview, "@onclick=\"CloseAsync\"");
        StringAssert.Contains(preview, "@onclick:stopPropagation=\"true\"");
        StringAssert.Contains(preview, "Image zoom controls");
        StringAssert.Contains(preview, "Zoom in");
        StringAssert.Contains(preview, "Zoom out");
        StringAssert.Contains(preview, "Reset zoom");
        StringAssert.Contains(preview, "\"ArrowLeft\"");
        StringAssert.Contains(preview, "\"ArrowRight\"");
    }

    [TestMethod]
    public void Overlay_InitializesWheelPinchAndDragPan()
    {
        var preview = Read("src", "Mesh.App", "Components", "Shared", "ImagePreview.razor");
        var script = Read("src", "Mesh.App", "wwwroot", "js", "mesh-ui.js");

        StringAssert.Contains(preview, "\"meshUI.initImagePreview\"");
        StringAssert.Contains(preview, "\"meshUI.imagePreviewCommand\"");
        StringAssert.Contains(preview, "\"meshUI.disposeImagePreview\"");
        StringAssert.Contains(script, "addEventListener('wheel'");
        StringAssert.Contains(script, "addEventListener('pointerdown'");
        StringAssert.Contains(script, "state.pointers.size === 2");
        StringAssert.Contains(script, "state.x += event.clientX - state.lastX");
        StringAssert.Contains(script, "Math.max(1, Math.min(8, value))");
    }

    [TestMethod]
    public void MessageImages_AreButtonsAndPreviewStaysOutsideWidgetSandbox()
    {
        var content = Read("src", "Mesh.App", "Components", "MessageContent.razor");
        var trigger = content.IndexOf("class=\"image-preview-trigger\"", StringComparison.Ordinal);
        var preview = content.IndexOf("<ImagePreview", StringComparison.Ordinal);
        var sandbox = content.IndexOf("sandbox=\"allow-scripts\"", StringComparison.Ordinal);

        Assert.IsTrue(trigger >= 0);
        StringAssert.Contains(content, "aria-label=\"Preview @attachment.Name\"");
        StringAssert.Contains(content, "aria-label=\"Preview @seg.FileName\"");
        Assert.IsTrue(preview > sandbox);

        var overlay = Read("src", "Mesh.App", "Components", "Shared", "ImagePreview.razor");
        Assert.IsFalse(overlay.Contains("iframe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(overlay.Contains("sandbox", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PlatformPolicy_UsesNativeShareOnlyOnAndroidAndIos()
    {
        Assert.AreEqual(ImageShareMode.NativeShare, ImageSharePolicy.Select(isAndroid: true, isIos: false));
        Assert.AreEqual(ImageShareMode.NativeShare, ImageSharePolicy.Select(isAndroid: false, isIos: true));
        Assert.AreEqual(ImageShareMode.BrowserDownload, ImageSharePolicy.Select(isAndroid: false, isIos: false));
    }

    [TestMethod]
    public void ShareImplementation_UsesTrustedBytesAndDoesNotLogBinaryData()
    {
        var service = Read("src", "Mesh.App", "Services", "ImageShareService.cs");
        StringAssert.Contains(service, "Convert.FromBase64String(request.Base64Data)");
        StringAssert.Contains(service, "Share.Default.RequestAsync");
        StringAssert.Contains(service, "\"meshUI.downloadFile\"");
        StringAssert.Contains(service, "StartsWith(\"image/\"");
        StringAssert.Contains(service, "ScheduleCleanup(directory)");
        Assert.IsFalse(service.Contains("http://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(service.Contains("https://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(service.Contains("Console.", StringComparison.Ordinal));
        Assert.IsFalse(service.Contains("ILogger", StringComparison.Ordinal));
    }

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
