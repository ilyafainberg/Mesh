using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Threading;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Mesh.App.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Mesh.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	// Held for the whole process lifetime so the installer (Inno Setup AppMutex) can detect a
	// running Mesh instance and close it via the Restart Manager during an update. Named to match
	// AppMutex in _deploy/mesh-client.iss.
	private static Mutex? singleInstanceMutex;

	// Windows App SDK single-instance key (distinct from the installer mutex above).
	private const string InstanceKey = "MeshApp.SingleInstance.WinAppSdk";

	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		try { singleInstanceMutex ??= new Mutex(initiallyOwned: false, "MeshApp.SingleInstance"); }
		catch { /* a mutex is best-effort; the updater also uses Restart Manager file detection */ }
		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	/// <summary>
	/// Enforces a single running instance and routes mesh:// protocol activations. A second launch
	/// forwards its activation (carrying any mesh:// URI) to the already-running instance and exits,
	/// so only one window ever exists. All of this is best-effort: if the single-instance APIs fail
	/// for any reason we fall through to a normal launch rather than blocking startup.
	/// </summary>
	protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
	{
		try
		{
			var appArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
			var primary = AppInstance.FindOrRegisterForKey(InstanceKey);
			if (!primary.IsCurrent)
			{
				// Another instance already owns the key: hand our activation to it, then exit.
				await primary.RedirectActivationToAsync(appArgs);
				Process.GetCurrentProcess().Kill();
				return;
			}

			// We are the primary instance: react to future redirected activations from new launches.
			primary.Activated += OnAppInstanceActivated;

			// Handle a cold-start activation: the app was launched by clicking a mesh:// link. For an
			// unpackaged app the URI arrives as a command-line argument, so scan those.
			DispatchFromCommandLine(Environment.GetCommandLineArgs());
		}
		catch
		{
			// Single-instancing is best-effort: never let it prevent the app from starting.
		}

		base.OnLaunched(args);
	}

	private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
		=> DispatchActivation(args);

	// Extracts a mesh:// link from a redirected activation (a second launch handed to us). Handles
	// both a rich Protocol activation and a plain Launch whose command line carries the URI.
	private static void DispatchActivation(AppActivationArguments args)
	{
		try
		{
			if (args.Kind == ExtendedActivationKind.Protocol
				&& args.Data is IProtocolActivatedEventArgs p && p.Uri is not null)
			{
				DeepLinkDispatch.Dispatch(p.Uri.ToString());
				return;
			}
			if (args.Kind == ExtendedActivationKind.Launch
				&& args.Data is ILaunchActivatedEventArgs l && !string.IsNullOrWhiteSpace(l.Arguments))
			{
				DispatchFromCommandLine(l.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
			}
		}
		catch { /* best-effort */ }
	}

	// Dispatches the first mesh:// token found among the given argument tokens.
	private static void DispatchFromCommandLine(IEnumerable<string> argv)
	{
		foreach (var a in argv)
		{
			var t = a.Trim().Trim('"');
			if (t.StartsWith("mesh://", StringComparison.OrdinalIgnoreCase))
			{
				DeepLinkDispatch.Dispatch(t);
				return;
			}
		}
	}
}

