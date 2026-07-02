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

		// Open at a comfortable default size instead of filling the screen.
		const double width = 1200, height = 820;
		window.Width = width;
		window.Height = height;
		try
		{
			var display = DeviceDisplay.Current.MainDisplayInfo;
			var dw = display.Width / display.Density;
			var dh = display.Height / display.Density;
			window.X = Math.Max(0, (dw - width) / 2);
			window.Y = Math.Max(0, (dh - height) / 2);
		}
		catch { /* fall back to platform default position */ }

		return window;
	}
}
