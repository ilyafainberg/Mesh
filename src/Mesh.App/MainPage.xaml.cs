using Microsoft.AspNetCore.Components.WebView;
using Mesh.App.Services;
#if IOS
using UIKit;
using WebKit;
#endif

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

    private void BlazorWebView_Initialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
#if IOS
        if (e.WebView is not WKWebView webView) return;

        // The ContentPage owns safe-area and keyboard resizing. Prevent WKWebView from applying a
        // second native inset while its HTML root fills the already-adjusted host bounds.
        var scrollView = webView.ScrollView;
        scrollView.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
        scrollView.AutomaticallyAdjustsScrollIndicatorInsets = false;
        scrollView.ContentInset = UIEdgeInsets.Zero;
        scrollView.ScrollIndicatorInsets = UIEdgeInsets.Zero;
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
