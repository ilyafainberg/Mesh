using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using Mesh.App.Services;
using ZXing.Net.Maui.Controls;
#if WINDOWS
using Microsoft.Maui.LifecycleEvents;
#endif

namespace Mesh.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		var diagnostics = new RuntimeDiagnostics(Path.Combine(StoragePaths.Root, "Diagnostics"));
		builder.Services.AddSingleton(diagnostics);
		diagnostics.StartSession(PlatformCaps.DevicePlatform, detectUnexpectedTermination: OperatingSystem.IsIOS());
		diagnostics.InstallManagedHandlers();
		builder.Logging.AddProvider(new RuntimeDiagnosticsLoggerProvider(diagnostics));

		builder
			.UseMauiApp<App>()
			.UseBarcodeReader()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

#if WINDOWS
		builder.ConfigureLifecycleEvents(events =>
		{
			events.AddWindows(w => w.OnWindowCreated(window =>
			{
				if (MeshProcessContext.IsHeadless)
					Mesh.App.Platforms.Windows.WindowsAppControl.AttachHeadless(window);
				else
				{
					Mesh.App.Platforms.Windows.WindowsAppControl.AttachTray(window);
					Mesh.App.Platforms.Windows.WindowsNotifier.Prime();
				}
			}));
		});
#endif

		// Parse --ui-mode from the command line before any service is registered so the
		// forced value is available when the App constructor and CreateWindow run.
		var uiModeOptions = UiModeParser.ParseArgs(Environment.GetCommandLineArgs());
		builder.Services.AddSingleton(uiModeOptions);
		builder.Services.AddSingleton<IUiModeService, UiModeService>();
		builder.Services.AddSingleton<MobileOverlayState>();

		builder.Services.AddMauiBlazorWebView();

		builder.Services.AddHttpClient();
		// Local models (Foundry, small CPU models) can take minutes per response,
		// well past the default 100s HttpClient timeout. Give model calls plenty of room.
		builder.Services.AddHttpClient("model", c => c.Timeout = TimeSpan.FromMinutes(10));
		builder.Services.AddHttpClient("connector");
		builder.Services.AddHttpClient("relay");
		builder.Services.AddSingleton(TimeProvider.System);
		// The self-updater downloads a large (hundreds of MB) client zip, so give it a generous
		// timeout and the User-Agent the GitHub API requires.
		builder.Services.AddHttpClient("updater", c =>
		{
			c.Timeout = TimeSpan.FromMinutes(30);
			c.DefaultRequestHeaders.UserAgent.ParseAdd("Mesh-Updater");
		});
		// The built-in skill catalog adapter fans one client out over skills.sh, agentskill.sh and
		// GitHub, setting per-request headers itself. A 12s timeout keeps search responsive.
		builder.Services.AddHttpClient("skillcatalog", c => c.Timeout = TimeSpan.FromSeconds(12));
#if IOS
		builder.Services.AddSingleton<ISecretStore, Mesh.App.Platforms.iOS.AppleSecretStore>();
#else
		builder.Services.AddSingleton<ISecretStore, SecretStore>();
#endif
		builder.Services.AddSingleton<IAppLifecycleState, AppLifecycleState>();
		builder.Services.AddSingleton<UiOperationCoordinator>();
		builder.Services.AddSingleton<ITopicSendIdentityStore>(_ =>
			new KeyValueTopicSendIdentityStore(
				key => Preferences.Default.Get(key, ""),
				(key, value) => Preferences.Default.Set(key, value),
				key => Preferences.Default.Remove(key)));
		builder.Services.AddSingleton<ITopicSendReconciliationQuery>(services =>
			new AppStateTopicSendReconciliationQuery(
				services.GetRequiredService<AppState>()));
		builder.Services.AddSingleton<TopicSendCoordinator>(services =>
			new TopicSendCoordinator(
				identityStore: services.GetRequiredService<ITopicSendIdentityStore>(),
				reconciliationQuery: services.GetRequiredService<ITopicSendReconciliationQuery>()));
		builder.Services.AddSingleton<ComposerRevisionGuard>();
#if WINDOWS
		builder.Services.AddSingleton<IAppControl, Mesh.App.Platforms.Windows.WindowsAppControl>();
		builder.Services.AddSingleton<INotifier, Mesh.App.Platforms.Windows.WindowsNotifier>();
		builder.Services.AddSingleton<IPushService, NoopPushService>();
#elif IOS
		builder.Services.AddSingleton<IAppControl, DefaultAppControl>();
		builder.Services.AddSingleton<INotifier, Mesh.App.Platforms.iOS.AppleNotifier>();
		builder.Services.AddSingleton<IPushService, Mesh.App.Platforms.iOS.ApplePushService>();
#elif ANDROID
		builder.Services.AddSingleton<IAppControl, DefaultAppControl>();
		builder.Services.AddSingleton<INotifier, Mesh.App.Platforms.Android.AndroidNotifier>();
		builder.Services.AddSingleton<IPushService, Mesh.App.Platforms.Android.FirebasePushService>();
#else
		builder.Services.AddSingleton<IAppControl, DefaultAppControl>();
		builder.Services.AddSingleton<INotifier, DefaultNotifier>();
		builder.Services.AddSingleton<IPushService, NoopPushService>();
#endif
#if WINDOWS
		builder.Services.AddSingleton<IAccountInstanceCoordinator, DesktopAccountInstanceCoordinator>();
#else
		builder.Services.AddSingleton<IAccountInstanceCoordinator, DefaultAccountInstanceCoordinator>();
#endif
		builder.Services.AddSingleton<AppState>();
		builder.Services.AddSingleton<INotificationState>(services => services.GetRequiredService<AppState>());
		builder.Services.AddSingleton<NotificationViewState>();
		builder.Services.AddSingleton<NotificationWakeSession>();
		builder.Services.AddSingleton<NotificationCoordinator>();
		builder.Services.AddSingleton<INotificationCoordinator>(services => services.GetRequiredService<NotificationCoordinator>());
		builder.Services.AddSingleton<IMessageClipboard>(_ => new MessageClipboard(
			markdown => Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default
				.SetTextAsync(markdown)));
		builder.Services.AddSingleton<IBuiltInContentProvider>(_ => new BuiltInContentProvider(
			path => FileSystem.Current.OpenAppPackageFileAsync(path),
			message => diagnostics.RecordEvent("built-in-content", message)));
		builder.Services.AddScoped<IImageShareService, ImageShareService>();
		builder.Services.AddSingleton<MobileOverlayState>();
		builder.Services.AddSingleton<IMemoryState>(services => services.GetRequiredService<AppState>());
		builder.Services.AddSingleton<TokenMeter>();
		builder.Services.AddSingleton<BrowserModelService>();
		builder.Services.AddSingleton<CopilotMcpBridge>();
		builder.Services.AddSingleton<CopilotAcpHost>();
		builder.Services.AddSingleton<ModelFactory>();
		builder.Services.AddSingleton<FoundryLocalService>();
		builder.Services.AddSingleton<MsalAuthService>();
		builder.Services.AddSingleton<ConnectorBroker>();
		builder.Services.AddSingleton<ConnectorCatalogService>();
		builder.Services.AddSingleton<GoogleAuthService>();
		builder.Services.AddSingleton<ConnectorAuthService>();
		builder.Services.AddSingleton<ToolApprovalService>();
		builder.Services.AddSingleton<LocationPermissionService>();
		builder.Services.AddSingleton<ToolRegistry>();
		builder.Services.AddSingleton<IQrScanner, QrScannerService>();
		builder.Services.AddSingleton<LocalFileRegistry>();
		builder.Services.AddSingleton<AgentMedia>();
		builder.Services.AddSingleton<McpHost>();
		builder.Services.AddSingleton<DocumentExtractor>();
		builder.Services.AddSingleton<SourceBrowser>();
		builder.Services.AddSingleton<FileImporter>();
		builder.Services.AddSingleton<AgentRunCoordinator>();
		builder.Services.AddSingleton<MemoryService>();
		builder.Services.AddSingleton<AgentService>();
		builder.Services.AddSingleton<TopicTurnRunner>();
		builder.Services.AddSingleton<ITopicTurnRunner>(services =>
			services.GetRequiredService<TopicTurnRunner>());
		builder.Services.AddSingleton<SkillMarketplaceService>();
		builder.Services.AddSingleton<SkillCatalogOptions>();
		builder.Services.AddSingleton<ISkillCatalogService>(services =>
		{
			var factory = services.GetRequiredService<System.Net.Http.IHttpClientFactory>();
			var options = services.GetRequiredService<SkillCatalogOptions>();
			return new SkillCatalogService(factory.CreateClient("skillcatalog"), options);
		});
		builder.Services.AddSingleton<ModelSetupService>();
		builder.Services.AddSingleton<UpdateService>();
		builder.Services.AddSingleton<MeshClient>();
		builder.Services.AddSingleton<IOnlineReplicationWakeTransport>(services =>
			services.GetRequiredService<MeshClient>());
		builder.Services.AddSingleton<OnlineReplicationWakeCoordinator>();
		builder.Services.AddSingleton<IDeviceTopicTransport>(services =>
			services.GetRequiredService<MeshClient>());
		builder.Services.AddSingleton<TopicExecutionRouter>();
		builder.Services.AddSingleton<ITopicExecutionRouter>(services =>
			services.GetRequiredService<TopicExecutionRouter>());
		builder.Services.AddSingleton<DirectoryClient>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		// Bind the singleton service to the static bridge so the Windows platform layer
		// can forward --ui-mode args from a second launch without a service-locator call.
		UiModeActivationBridge.Register(app.Services.GetRequiredService<IUiModeService>());
		OnlineReplicationWakeBridge.Register(app.Services.GetRequiredService<OnlineReplicationWakeCoordinator>());
		var notificationCoordinator = app.Services.GetRequiredService<NotificationCoordinator>();
		NotificationCoordinatorBridge.Register(notificationCoordinator);
		NotificationCoordinatorBridge.RecoverPending();
		NotificationWakeSessionBridge.Register(app.Services.GetRequiredService<NotificationWakeSession>());
		NotificationNavigationBridge.Register(notificationCoordinator.GetHighestPriorityRouteAsync);
		PushRegistrationBridge.Register(ct =>
			app.Services.GetRequiredService<MeshClient>().RegisterPushTokenAsync(ct));


#if WINDOWS
		MeshDesktopInstanceRuntime.AttachHost(
			arguments =>
			{
				Mesh.App.Platforms.Windows.WindowsAppControl.Activate(arguments);
				return Task.CompletedTask;
			},
			async ct =>
			{
				var mesh = app.Services.GetRequiredService<MeshClient>();
				mesh.BeginShutdown();
				await app.Services.GetRequiredService<AppState>().FlushPersistenceAsync(ct).ConfigureAwait(false);
				await mesh.DisconnectAsync().WaitAsync(ct).ConfigureAwait(false);
			},
			Mesh.App.Platforms.Windows.WindowsAppControl.ExitNow);
#endif

		// Auto-update marketplace-imported skills in the background at startup (never blocks launch).
		_ = Task.Run(async () =>
		{
			try
			{
				await app.Services.GetRequiredService<SkillMarketplaceService>().SyncAllAsync();
			}
			catch (Exception ex) { RuntimeDiagnostics.Current?.RecordException("marketplace-startup-sync", ex); }
		});
		return app;
	}

}
