using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class PickedFileStagerTests
{
    [TestMethod]
    public async Task StageAsync_CopiesProviderStreamIntoOwnedFile()
    {
        var root = NewRoot();
        try
        {
            await using var source = new MemoryStream([1, 2, 3, 4]);
            var staged = await PickedFileStager.StageAsync("notes.txt", "text/plain", source, root, 32);

            Assert.AreEqual("notes.txt", staged.Name);
            Assert.AreEqual(4, staged.Size);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(staged.Path));
            StringAssert.StartsWith(Path.GetFullPath(staged.Path), Path.GetFullPath(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageAsync_RejectsOversizeAndRemovesPartialFile()
    {
        var root = NewRoot();
        try
        {
            await using var source = new MemoryStream([1, 2, 3, 4]);
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => PickedFileStager.StageAsync("large.bin", null, source, root, 3));

            StringAssert.Contains(error.Message, "20 MB attachment limit");
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageAsync_RejectsEmptyFileAndRemovesPlaceholder()
    {
        var root = NewRoot();
        try
        {
            await using var source = new MemoryStream();
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => PickedFileStager.StageAsync("empty.txt", "text/plain", source, root, 32));

            Assert.AreEqual("That file is empty.", error.Message);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "MeshPickedFileTests", Guid.NewGuid().ToString("n"));
}
