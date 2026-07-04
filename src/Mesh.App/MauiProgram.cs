using Microsoft.Extensions.Logging;
using Mesh.App.Services;
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
			}));
		});
#endif

		builder.Services.AddMauiBlazorWebView();

		builder.Services.AddHttpClient();
		// Local models (Foundry, small CPU models) can take minutes per response,
		// well past the default 100s HttpClient timeout. Give model calls plenty of room.
		builder.Services.AddHttpClient("model", c => c.Timeout = TimeSpan.FromMinutes(10));
		builder.Services.AddHttpClient("connector");
		builder.Services.AddSingleton<ISecretStore, SecretStore>();
#if WINDOWS
		builder.Services.AddSingleton<IAppControl, Mesh.App.Platforms.Windows.WindowsAppControl>();
#else
		builder.Services.AddSingleton<IAppControl, DefaultAppControl>();
#endif
		builder.Services.AddSingleton<AppState>();
		builder.Services.AddSingleton<TokenMeter>();
		builder.Services.AddSingleton<ModelFactory>();
		builder.Services.AddSingleton<FoundryLocalService>();
		builder.Services.AddSingleton<MsalAuthService>();
		builder.Services.AddSingleton<ConnectorBroker>();
		builder.Services.AddSingleton<GoogleAuthService>();
		builder.Services.AddSingleton<ConnectorAuthService>();
		builder.Services.AddSingleton<ToolRegistry>();
		builder.Services.AddSingleton<DocumentExtractor>();
		builder.Services.AddSingleton<SourceBrowser>();
		builder.Services.AddSingleton<FileImporter>();
		builder.Services.AddSingleton<AgentService>();
		builder.Services.AddSingleton<ModelSetupService>();
		builder.Services.AddSingleton<MeshClient>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
