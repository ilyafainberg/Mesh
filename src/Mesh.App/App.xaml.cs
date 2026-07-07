namespace Mesh.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "Mesh" };

		// Restore the last size and position, or open at the 1470 x 350 default, centered.
		Mesh.App.Services.WindowGeometry.Apply(window);

		// Persist geometry when the window loses focus and when it is torn down, so we reopen
		// where the user left it. (Deactivated fires often but a Preferences write is cheap.)
		window.Deactivated += (_, _) => Mesh.App.Services.WindowGeometry.Save(window);
		window.Destroying += (_, _) => Mesh.App.Services.WindowGeometry.Save(window);

		return window;
	}
}
