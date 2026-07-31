namespace Mesh.App.Services;

public enum ImageShareMode
{
    BrowserDownload,
    NativeShare
}

public static class ImageSharePolicy
{
    public static ImageShareMode Select(bool isAndroid, bool isIos)
        => isAndroid || isIos ? ImageShareMode.NativeShare : ImageShareMode.BrowserDownload;
}
