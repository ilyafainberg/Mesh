#if IOS
using Foundation;
#endif

namespace Mesh.App.Services;

public static class StorageProtection
{
    /// <summary>Makes local state readable after first unlock so a suspended iOS app can synchronize it.</summary>
    public static void TryEnsureBackgroundReadable(string path)
    {
#if IOS
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        try
        {
            var attributes = new NSFileAttributes
            {
                ProtectionKey = NSFileProtection.CompleteUntilFirstUserAuthentication
            };
            if (!NSFileManager.DefaultManager.SetAttributes(attributes, path, out var error))
                RuntimeDiagnostics.Current?.RecordEvent(
                    "ios-file-protection", error?.LocalizedDescription ?? "attribute update failed");
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("ios-file-protection", ex);
        }
#endif
    }
}
