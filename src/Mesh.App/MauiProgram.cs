using Microsoft.Extensions.Logging;
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
		builder
			.UseMauiApp<App>()
			.UseBarcodeReader()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

#if WINDOWS
		// Minimize/close to the system tray and expose a real quit.
		builder.ConfigureLifecycleEvents(events =>
		{
			events.AddWindows(w => w.OnWindowCreated(window =>
			{
				Mesh.App.Platforms.Windows.WindowsAppControl.AttachTray(window);
				// Register the toast manager up front so the first notification actually pops
				// (unpackaged apps drop toasts shown before registration completes).
				Mesh.App.Platforms.Windows.WindowsNotifier.Prime();
			}));
		});
#endif

		// Parse --ui-mode from the command line before any service is registered so the
		// forced value is available when the App constructor and CreateWindow run.
		var uiModeOptions = UiModeParser.ParseArgs(Environment.GetCommandLineArgs());
		builder.Services.AddSingleton(uiModeOptions);
		builder.Services.AddSingleton<IUiModeService, UiModeService>();

		builder.Services.AddMauiBlazorWebView();

		builder.Services.AddHttpClient();
		// Local models (Foundry, small CPU models) can take minutes per response,
		// well past the default 100s HttpClient timeout. Give model calls plenty of room.
		builder.Services.AddHttpClient("model", c => c.Timeout = TimeSpan.FromMinutes(10));
		builder.Services.AddHttpClient("connector");
		builder.Services.AddHttpClient("relay");
		// The self-updater downloads a large (hundreds of MB) client zip, so give it a generous
		// timeout and the User-Agent the GitHub API requires.
		builder.Services.AddHttpClient("updater", c =>
		{
			c.Timeout = TimeSpan.FromMinutes(30);
			c.DefaultRequestHeaders.UserAgent.ParseAdd("Mesh-Updater");
		});
		builder.Services.AddSingleton<ISecretStore, SecretStore>();
#if WINDOWS
		builder.Services.AddSingleton<IAppControl, Mesh.App.Platforms.Windows.WindowsAppControl>();
		builder.Services.AddSingleton<INotifier, Mesh.App.Platforms.Windows.WindowsNotifier>();
		builder.Services.AddSingleton<IPushService, NoopPushService>();
#elif IOS
		builder.Services.AddSingleton<IAppControl, DefaultAppControl>();
		builder.Services.AddSingleton<INotifier, DefaultNotifier>();
		builder.Services.AddSingleton<IPushService, Mesh.App.Platforms.iOS.ApplePushService>();
#elif ANDROID
		builder.Services.AddSingleton<IAppControl, DefaultAppControl>();
		builder.Services.AddSingleton<INotifier, DefaultNotifier>();
		builder.Services.AddSingleton<IPushService, Mesh.App.Platforms.Android.FirebasePushService>();
#else
		builder.Services.AddSingleton<IAppControl, DefaultAppControl>();
		builder.Services.AddSingleton<INotifier, DefaultNotifier>();
		builder.Services.AddSingleton<IPushService, NoopPushService>();
#endif
		builder.Services.AddSingleton<AppState>();
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
		builder.Services.AddSingleton<AgentService>();
		builder.Services.AddSingleton<SkillMarketplaceService>();
		builder.Services.AddSingleton<ModelSetupService>();
		builder.Services.AddSingleton<UpdateService>();
		builder.Services.AddSingleton<MeshClient>();
		builder.Services.AddSingleton<DirectoryClient>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		// Bind the singleton service to the static bridge so the Windows platform layer
		// can forward --ui-mode args from a second launch without a service-locator call.
		UiModeActivationBridge.Register(app.Services.GetRequiredService<IUiModeService>());

		// Auto-update marketplace-imported skills in the background at startup (never blocks launch).
		_ = Task.Run(async () =>
		{
			try { await app.Services.GetRequiredService<SkillMarketplaceService>().SyncAllAsync(); }
			catch { /* startup sync is best-effort */ }
		});
		return app;
	}
}
