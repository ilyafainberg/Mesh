namespace Mesh.App.Services;

/// <summary>Resolves the native parent window handle for interactive MSAL sign-in.</summary>
public static class ParentWindow
{
    public static IntPtr GetHandle()
    {
#if WINDOWS
        var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault();
        var native = mauiWindow?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (native is not null)
            return WinRT.Interop.WindowNative.GetWindowHandle(native);
#endif
        return IntPtr.Zero;
    }
}
