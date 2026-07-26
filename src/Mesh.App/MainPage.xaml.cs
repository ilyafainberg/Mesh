using Microsoft.AspNetCore.Components.WebView;
using Mesh.App.Services;

namespace Mesh.App;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
#if IOS
        // Match the exposed iOS safe areas to the white mobile navigation surface.
        BackgroundColor = Colors.White;
        blazorWebView.BackgroundColor = Colors.White;
        SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.All;
#endif
    }

    private async void BlazorWebView_UrlLoading(object? sender, UrlLoadingEventArgs e)
    {
        if (!e.Url.IsFile)
            return;

        e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;

        try
        {
            await LocalFileLauncher.OpenAsync(e.Url.AbsoluteUri);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not open file", ex.Message, "OK");
        }
    }
}
