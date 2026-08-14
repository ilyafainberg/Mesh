using BackgroundTasks;
using Foundation;
using MetricKit;
using Mesh.App.Platforms.iOS;
using Mesh.App.Services;
using Mesh.Shared;
using Microsoft.Identity.Client;
using ObjCRuntime;
using System.Text;
using UIKit;
using UserNotifications;

namespace Mesh.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    private const string BackgroundRefreshTaskIdentifier = "net.meshrelay.mesh.sync.refresh";
    private static readonly NSString MeshPayloadKey = new("mesh");
    private static readonly NSString MeshTypeKey = new("type");
    private static readonly NSString MeshVersionKey = new("v");
    private static readonly NSString FlatMeshTypeKey = new("mesh_type");
    private static readonly NSString FlatMeshVersionKey = new("mesh_v");
    private static readonly NSString WakeIdKey = new("wake_id");
    private static readonly NSString ApsKey = new("aps");
    private static readonly NSString AlertKey = new("alert");
    private readonly MeshNotificationCenterDelegate notificationCenterDelegate = new();
    private MeshMetricManagerSubscriber? metricSubscriber;
    private NSObject? memoryWarningObserver;
    private bool nativeExceptionHooksInstalled;

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        AppLifecycleState.SetForeground(application.ApplicationState == UIApplicationState.Active);

        // Present relay-composed alerts even while the app is foregrounded (iOS suppresses them by default).
        // UNUserNotificationCenter holds a weak delegate, so AppDelegate keeps the managed instance alive.
        UNUserNotificationCenter.Current.Delegate = notificationCenterDelegate;
        var launched = base.FinishedLaunching(application, launchOptions);
        RegisterBackgroundRefreshTask();

        var diagnostics = RuntimeDiagnostics.Current;
        diagnostics?.MarkLifecycle("launched");
        if (diagnostics is not null)
        {
            InstallNativeExceptionHooks();
            InstallMetricKit(diagnostics);
            memoryWarningObserver = UIApplication.Notifications.ObserveDidReceiveMemoryWarning(
                (_, _) => RecordMemoryWarning(diagnostics));
        }
        return launched;
    }

    public override void OnActivated(UIApplication application)
    {
        AppLifecycleState.SetForeground(true);
        RuntimeDiagnostics.Current?.MarkLifecycle("active");
        base.OnActivated(application);
    }

    public override void OnResignActivation(UIApplication application)
    {
        RuntimeDiagnostics.Current?.MarkLifecycle("inactive");
        base.OnResignActivation(application);
    }

    public override void DidEnterBackground(UIApplication application)
    {
        AppLifecycleState.SetForeground(false);
        RuntimeDiagnostics.Current?.MarkLifecycle("background");
        ScheduleBackgroundRefresh();
        base.DidEnterBackground(application);
    }

    public override void WillEnterForeground(UIApplication application)
    {
        RuntimeDiagnostics.Current?.MarkLifecycle("foreground");
        base.WillEnterForeground(application);
    }

    [Export("application:didReceiveRemoteNotification:fetchCompletionHandler:")]
    public void DidReceiveRemoteNotification(
        UIApplication application,
        NSDictionary userInfo,
        Action<UIBackgroundFetchResult> completionHandler)
    {
        if (!TryGetMeshSyncNotification(userInfo, out var wakeId, out var hasVisibleAlert))
        {
            completionHandler(UIBackgroundFetchResult.NoData);
            return;
        }
        var visibleRemoteAlert = RemoteWakeNotificationPolicy.ShouldShowGenericAlert(
            hasVisibleAlert, application.ApplicationState == UIApplicationState.Active);
        _ = CompleteRemoteNotificationSyncAsync(wakeId, visibleRemoteAlert, completionHandler);
    }

    private static async Task CompleteRemoteNotificationSyncAsync(
        string? wakeId,
        bool visibleRemoteAlert,
        Action<UIBackgroundFetchResult> completionHandler)
    {
        using var wakeSession = NotificationWakeSessionBridge.Begin(wakeId, visibleRemoteAlert);
        try
        {
            var result = await OnlineReplicationWakeBridge.SynchronizePendingAsync().ConfigureAwait(false);
            completionHandler(result.Outcome switch
            {
                OnlineReplicationWakeOutcome.NewData => UIBackgroundFetchResult.NewData,
                OnlineReplicationWakeOutcome.NoData => UIBackgroundFetchResult.NoData,
                _ => UIBackgroundFetchResult.Failed
            });
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("background-push-sync", ex);
            completionHandler(UIBackgroundFetchResult.Failed);
        }
    }

    internal static bool TryGetMeshSyncNotification(
        NSDictionary userInfo,
        out string? wakeId,
        out bool hasVisibleAlert)
    {
        wakeId = null;
        hasVisibleAlert = userInfo[ApsKey] is NSDictionary aps && aps[AlertKey] is not null;
        var type = userInfo[FlatMeshTypeKey]?.ToString();
        var versionValue = userInfo[FlatMeshVersionKey];
        if (string.Equals(type, "sync", StringComparison.Ordinal)
            && TryReadProtocolVersion(versionValue, out var flatVersion))
        {
            wakeId = userInfo[WakeIdKey]?.ToString();
            return flatVersion == MeshProtocol.Version;
        }

        if (userInfo[MeshPayloadKey] is not NSDictionary mesh
            || !string.Equals(mesh[MeshTypeKey]?.ToString(), "sync", StringComparison.Ordinal)
            || !TryReadProtocolVersion(mesh[MeshVersionKey], out var nestedVersion)
            || nestedVersion != MeshProtocol.Version)
            return false;
        wakeId = mesh[WakeIdKey]?.ToString();
        return true;
    }

    private static bool TryReadProtocolVersion(NSObject? value, out int version)
    {
        if (value is NSNumber number)
        {
            version = number.Int32Value;
            return true;
        }
        return int.TryParse(value?.ToString(), out version);
    }

    private static void RegisterBackgroundRefreshTask()
    {
        try
        {
            var registered = BGTaskScheduler.Shared.Register(
                BackgroundRefreshTaskIdentifier,
                null,
                task => _ = RunBackgroundRefreshAsync(task));
            if (!registered)
                RuntimeDiagnostics.Current?.RecordEvent("background-refresh", "registration rejected");
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("background-refresh-registration", ex);
        }
    }

    private static void ScheduleBackgroundRefresh()
    {
        try
        {
            BGTaskScheduler.Shared.Cancel(BackgroundRefreshTaskIdentifier);
            using var request = new BGAppRefreshTaskRequest(BackgroundRefreshTaskIdentifier)
            {
                EarliestBeginDate = NSDate.FromTimeIntervalSinceNow(TimeSpan.FromMinutes(15).TotalSeconds)
            };
            if (!BGTaskScheduler.Shared.Submit(request, out var error))
                RuntimeDiagnostics.Current?.RecordEvent(
                    "background-refresh", error?.LocalizedDescription ?? "schedule rejected");
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("background-refresh-schedule", ex);
        }
    }

    private static async Task RunBackgroundRefreshAsync(BGTask task)
    {
        using var expired = new CancellationTokenSource();
        task.ExpirationHandler = expired.Cancel;
        try
        {
            var result = await OnlineReplicationWakeBridge.SynchronizePendingAsync(
                OnlineReplicationWakeCoordinator.DefaultBudget, expired.Token).ConfigureAwait(false);
            task.SetTaskCompleted(result.Outcome != OnlineReplicationWakeOutcome.Failed);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("background-refresh-run", ex);
            task.SetTaskCompleted(false);
        }
        finally
        {
            task.ExpirationHandler = null;
            ScheduleBackgroundRefresh();
        }
    }

    private static void RecordMemoryWarning(RuntimeDiagnostics diagnostics)
    {
        try
        {
            var memory = GC.GetGCMemoryInfo();
            diagnostics.RecordEvent(
                "ios-memory-warning",
                $"managedBytes={GC.GetTotalMemory(forceFullCollection: false)}; heapBytes={memory.HeapSizeBytes}; "
                + $"memoryLoadBytes={memory.MemoryLoadBytes}; highThresholdBytes={memory.HighMemoryLoadThresholdBytes}");
        }
        catch (Exception ex)
        {
            diagnostics.RecordException("ios-memory-warning-callback", ex);
        }
    }

    public override void WillTerminate(UIApplication application)
    {
        var diagnostics = RuntimeDiagnostics.Current;
        diagnostics?.MarkLifecycle("terminated");
        if (metricSubscriber is not null)
        {
            try
            {
                MXMetricManager.SharedManager.Remove(metricSubscriber);
            }
            catch (Exception ex)
            {
                diagnostics?.RecordException("metrickit-remove", ex);
            }
            finally
            {
                metricSubscriber.Dispose();
                metricSubscriber = null;
            }
        }
        memoryWarningObserver?.Dispose();
        memoryWarningObserver = null;
        RemoveNativeExceptionHooks();
        base.WillTerminate(application);
    }

    private void InstallNativeExceptionHooks()
    {
        if (nativeExceptionHooksInstalled) return;
        nativeExceptionHooksInstalled = true;
        ObjCRuntime.Runtime.MarshalManagedException += OnMarshalManagedException;
        ObjCRuntime.Runtime.MarshalObjectiveCException += OnMarshalObjectiveCException;
    }

    private void RemoveNativeExceptionHooks()
    {
        if (!nativeExceptionHooksInstalled) return;
        nativeExceptionHooksInstalled = false;
        ObjCRuntime.Runtime.MarshalManagedException -= OnMarshalManagedException;
        ObjCRuntime.Runtime.MarshalObjectiveCException -= OnMarshalObjectiveCException;
    }

    private void OnMarshalManagedException(object? sender, MarshalManagedExceptionEventArgs args)
        => RuntimeDiagnostics.Current?.RecordException("ios-managed-native-boundary", args.Exception);

    private void OnMarshalObjectiveCException(object? sender, MarshalObjectiveCExceptionEventArgs args)
        => RuntimeDiagnostics.Current?.RecordEvent(
            "ios-objective-c-exception", args.Exception?.ToString() ?? "unknown Objective-C exception");

    private void InstallMetricKit(RuntimeDiagnostics diagnostics)
    {
        var added = false;
        try
        {
            metricSubscriber = new MeshMetricManagerSubscriber(diagnostics);
            MXMetricManager.SharedManager.Add(metricSubscriber);
            added = true;
            metricSubscriber.DidReceiveDiagnosticPayloads(MXMetricManager.SharedManager.PastDiagnosticPayloads);
        }
        catch (Exception ex)
        {
            diagnostics.RecordException("metrickit-startup", ex);
            if (added && metricSubscriber is not null)
            {
                try
                {
                    MXMetricManager.SharedManager.Remove(metricSubscriber);
                }
                catch (Exception removeException)
                {
                    diagnostics.RecordException("metrickit-remove", removeException);
                }
            }
            metricSubscriber?.Dispose();
            metricSubscriber = null;
        }
    }

    // APNs issued this device a token: forward it to ApplePushService so the pending RegisterAsync completes.
    // The token is emitted as a lowercase hex string, which is exactly what the relay's APNs sender targets.
    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
    {
        try
        {
            var bytes = deviceToken.ToArray();
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) hex.Append(value.ToString("x2"));
            ApplePushService.CompleteRegistration(hex.ToString());
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("apns-token-callback", ex);
            ApplePushService.FailRegistration("APNs token callback failed.");
        }
    }

    // APNs registration failed (missing entitlement, no network, restricted state): unblock any pending
    // RegisterAsync with a null token so the client simply proceeds without push.
    [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
    public void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error)
    {
        try
        {
            var reason = error.LocalizedDescription;
            RuntimeDiagnostics.Current?.RecordEvent("apns-registration-failed", reason);
            ApplePushService.FailRegistration(reason);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("apns-failure-callback", ex);
            ApplePushService.FailRegistration("APNs failure callback failed.");
        }
    }

    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        var value = url.AbsoluteString;
        if (value?.StartsWith(MsalAuthService.MobileRedirectUri, StringComparison.OrdinalIgnoreCase) == true)
            return AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(url);

        if (base.OpenUrl(application, url, options))
            return true;

        if (value?.StartsWith("mesh://", StringComparison.OrdinalIgnoreCase) == true)
        {
            DeepLinkDispatch.Dispatch(value);
            return true;
        }
        return false;
    }

    public override bool ContinueUserActivity(
        UIApplication application,
        NSUserActivity userActivity,
        UIApplicationRestorationHandler completionHandler)
    {
        var value = userActivity.WebPageUrl?.AbsoluteString;
        if (value?.StartsWith("https://meshrelay.net/link", StringComparison.OrdinalIgnoreCase) == true)
        {
            DeepLinkDispatch.Dispatch(value);
            return true;
        }
        return base.ContinueUserActivity(application, userActivity, completionHandler);
    }
}

public sealed class MeshMetricManagerSubscriber(RuntimeDiagnostics diagnostics)
    : NSObject, IMXMetricManagerSubscriber
{
    public void DidReceiveMetricPayloads(MXMetricPayload[] payloads)
    {
        // Runtime metrics are intentionally not retained. Diagnostic payloads contain the actionable data.
    }

    public void DidReceiveDiagnosticPayloads(MXDiagnosticPayload[] payloads)
    {
        foreach (var payload in payloads ?? [])
        {
            try
            {
                var json = Encoding.UTF8.GetString(payload.JsonRepresentation.ToArray());
                diagnostics.RecordDiagnosticPayload("metrickit", json);
            }
            catch (Exception ex)
            {
                diagnostics.RecordException("metrickit-payload", ex);
            }
        }
    }
}

// Suppresses contentless sync alerts in the foreground; committed local notifications are presented.
public sealed class MeshNotificationCenterDelegate : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(
        UNUserNotificationCenter center,
        UNNotification notification,
        Action<UNNotificationPresentationOptions> completionHandler)
    {
        if (AppDelegate.TryGetMeshSyncNotification(
                notification.Request.Content.UserInfo,
                out _,
                out _))
        {
            completionHandler(UNNotificationPresentationOptions.None);
            return;
        }

        completionHandler(
            UNNotificationPresentationOptions.Banner
            | UNNotificationPresentationOptions.Sound
            | UNNotificationPresentationOptions.Badge);
    }

    public override void DidReceiveNotificationResponse(
        UNUserNotificationCenter center,
        UNNotificationResponse response,
        Action completionHandler)
        => _ = CompleteActivationAsync(response, completionHandler);

    private static async Task CompleteActivationAsync(
        UNNotificationResponse response,
        Action completionHandler)
    {
        try
        {
            var userInfo = response.Notification.Request.Content.UserInfo;
            if (AppDelegate.TryGetMeshSyncNotification(userInfo, out _, out _))
                await NotificationNavigationBridge.OpenHighestPriorityAfterSyncAsync().ConfigureAwait(false);
            else if (userInfo[new NSString(AppleNotifier.RouteKey)]?.ToString() is { Length: > 0 } route)
                DeepLinkDispatch.Dispatch(route);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("notification-activation", ex);
        }
        finally
        {
            completionHandler();
        }
    }
}
