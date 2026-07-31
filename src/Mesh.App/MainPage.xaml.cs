using Microsoft.AspNetCore.Components.WebView;
#if ANDROID
using Android.Webkit;
using AndroidWebView = Android.Webkit.WebView;
#elif IOS || MACCATALYST
using Foundation;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
using WebKit;
#elif WINDOWS
using Microsoft.Web.WebView2.Core;
using WinUIWebView2 = Microsoft.UI.Xaml.Controls.WebView2;
#endif
using Mesh.App.Services;

namespace Mesh.App;

public partial class MainPage : ContentPage
{
#if ANDROID
    private WebViewClient? widgetWebViewClient;
#elif IOS || MACCATALYST
    private WKNavigationDelegate? widgetNavigationDelegate;
#elif WINDOWS
    private CoreWebView2? widgetWebView2;
#endif

    public MainPage()
    {
        InitializeComponent();
#if IOS
        // Match the exposed iOS safe areas to the white mobile navigation surface.
        BackgroundColor = Colors.White;
        blazorWebView.BackgroundColor = Colors.White;
        On<iOS>().SetUseSafeArea(true);
#endif
    }

    private async void BlazorWebView_Loaded(object? sender, EventArgs e)
    {
#if ANDROID
        if (blazorWebView.Handler?.PlatformView is not AndroidWebView webView
            || webView.WebViewClient is not { } current
            || current is WidgetWebViewClient)
            return;

        widgetWebViewClient = new WidgetWebViewClient(current);
        webView.SetWebViewClient(widgetWebViewClient);
#elif IOS || MACCATALYST
        if (blazorWebView.Handler?.PlatformView is not WKWebView webView
            || webView.NavigationDelegate is not WKNavigationDelegate current
            || current is WidgetNavigationDelegate)
            return;

        widgetNavigationDelegate = new WidgetNavigationDelegate(current);
        webView.NavigationDelegate = widgetNavigationDelegate;
#elif WINDOWS
        if (blazorWebView.Handler?.PlatformView is not WinUIWebView2 webView)
            return;
        await webView.EnsureCoreWebView2Async();
        if (ReferenceEquals(widgetWebView2, webView.CoreWebView2))
            return;
        if (widgetWebView2 is not null)
            widgetWebView2.FrameNavigationStarting -= OnFrameNavigationStarting;
        widgetWebView2 = webView.CoreWebView2;
        widgetWebView2.FrameNavigationStarting += OnFrameNavigationStarting;
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

#if ANDROID
    private sealed class WidgetWebViewClient(WebViewClient inner) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(
            AndroidWebView? view,
            IWebResourceRequest? request)
        {
            if (request is { IsForMainFrame: false }
                && !IsAllowedEmbeddedNavigation(request.Url?.ToString()))
            {
                RecordBlockedWidgetNavigation(request.Url?.Scheme, request.Url?.Host);
                return true;
            }
            return inner.ShouldOverrideUrlLoading(view, request);
        }

        public override WebResourceResponse? ShouldInterceptRequest(
            AndroidWebView? view,
            IWebResourceRequest? request)
            => inner.ShouldInterceptRequest(view, request);

        public override void OnPageFinished(AndroidWebView? view, string? url)
            => inner.OnPageFinished(view, url);

        public override void DoUpdateVisitedHistory(
            AndroidWebView? view,
            string? url,
            bool isReload)
            => inner.DoUpdateVisitedHistory(view, url, isReload);
    }
#elif IOS || MACCATALYST
    private sealed class WidgetNavigationDelegate(WKNavigationDelegate inner) : WKNavigationDelegate
    {
        public override void DecidePolicy(
            WKWebView webView,
            WKNavigationAction navigationAction,
            Action<WKNavigationActionPolicy> decisionHandler)
        {
            var fromSubframe = !navigationAction.SourceFrame.MainFrame;
            var toSubframe = navigationAction.TargetFrame is { MainFrame: false };
            var requestedUrl = navigationAction.Request.Url;
            var navigationKind = GetAllowedWidgetNavigationKind(
                navigationAction.SourceFrame.Request.Url,
                requestedUrl,
                fromSubframe,
                toSubframe);

            if (navigationKind is not null)
            {
                RecordWidgetNavigation("allowed", requestedUrl, navigationKind);
                decisionHandler(WKNavigationActionPolicy.Allow);
                return;
            }

            if (fromSubframe || toSubframe)
            {
                RecordWidgetNavigation("blocked", requestedUrl, "external-subframe");
                decisionHandler(WKNavigationActionPolicy.Cancel);
                return;
            }

            // Main-frame navigation remains owned by MAUI.
            inner.DecidePolicy(webView, navigationAction, decisionHandler);
        }

        public override void DidStartProvisionalNavigation(WKWebView webView, WKNavigation navigation)
            => inner.DidStartProvisionalNavigation(webView, navigation);

        public override void DidReceiveServerRedirectForProvisionalNavigation(
            WKWebView webView,
            WKNavigation navigation)
            => inner.DidReceiveServerRedirectForProvisionalNavigation(webView, navigation);

        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error)
            => inner.DidFailNavigation(webView, navigation, error);

        public override void DidFailProvisionalNavigation(
            WKWebView webView,
            WKNavigation navigation,
            NSError error)
            => inner.DidFailProvisionalNavigation(webView, navigation, error);

        public override void DidCommitNavigation(WKWebView webView, WKNavigation navigation)
            => inner.DidCommitNavigation(webView, navigation);

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
            => inner.DidFinishNavigation(webView, navigation);

        private static string? GetAllowedWidgetNavigationKind(
            NSUrl? current,
            NSUrl? requested,
            bool fromSubframe,
            bool toSubframe)
        {
            if (fromSubframe && IsSameDocumentFragmentNavigation(current, requested))
                return "same-document-fragment";
            if (!toSubframe)
                return null;

            var value = requested?.AbsoluteString;
            if (string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase))
                return "about-blank";
            if (string.Equals(value, "about:srcdoc", StringComparison.OrdinalIgnoreCase))
                return "about-srcdoc";
            if (string.Equals(requested?.Scheme, "app", StringComparison.OrdinalIgnoreCase)
                && string.Equals(requested?.Host, "0.0.0.1", StringComparison.OrdinalIgnoreCase)
                && string.Equals(requested?.Path, "/widget-host.html", StringComparison.Ordinal)
                && string.IsNullOrEmpty(requested?.Query))
                return "widget-host";

            return null;
        }

        private static bool IsSameDocumentFragmentNavigation(NSUrl? current, NSUrl? requested)
        {
            var currentValue = current?.AbsoluteString;
            var requestedValue = requested?.AbsoluteString;
            if (string.IsNullOrEmpty(currentValue)
                || string.IsNullOrEmpty(requestedValue)
                || requestedValue.IndexOf('#') < 0)
                return false;

            return string.Equals(
                WithoutFragment(currentValue),
                WithoutFragment(requestedValue),
                StringComparison.Ordinal);
        }

        private static string WithoutFragment(string value)
        {
            var fragment = value.IndexOf('#');
            return fragment < 0 ? value : value[..fragment];
        }
    }
#elif WINDOWS
    private void OnFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedEmbeddedNavigation(e.Uri))
            return;
        e.Cancel = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            RecordBlockedWidgetNavigation(uri.Scheme, uri.Host);
        else
            RecordBlockedWidgetNavigation(null, null);
    }
#endif

#if ANDROID || WINDOWS
    private static bool IsAllowedEmbeddedNavigation(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(uri.OriginalString, "about:blank", StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.OriginalString, "about:srcdoc", StringComparison.OrdinalIgnoreCase);
    }
#endif

#if IOS || MACCATALYST
    private static void RecordWidgetNavigation(string outcome, NSUrl? url, string kind)
        => RuntimeDiagnostics.Current?.RecordEvent(
            $"widget-navigation-{outcome}",
            $"kind={kind}; scheme={url?.Scheme ?? "unknown"}; host={url?.Host ?? "none"}");
#endif

#if ANDROID || WINDOWS
    private static void RecordBlockedWidgetNavigation(string? scheme, string? host)
        => RuntimeDiagnostics.Current?.RecordEvent(
            "widget-navigation-blocked",
            $"scheme={scheme ?? "unknown"}; host={host ?? "none"}");
#endif
}
