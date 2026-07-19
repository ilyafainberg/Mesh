using Android.App;
using Android.Content;
using Android.Content.PM;
using Microsoft.Identity.Client;

namespace Mesh.App;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "mesh",
    DataHost = "oauth",
    DataPathPrefix = "/google")]
public sealed class WebAuthenticatorCallbackActivity
    : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "msal562957d8-0f97-47eb-a445-a93d4a938f5a",
    DataHost = "auth")]
public sealed class MsalCallbackActivity : BrowserTabActivity;
