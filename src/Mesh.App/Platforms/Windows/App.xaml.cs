using Microsoft.UI.Xaml;
using System.Threading;

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
}

