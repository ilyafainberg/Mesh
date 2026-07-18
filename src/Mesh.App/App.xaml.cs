using Mesh.App.Services;

namespace Mesh.App;

public partial class App : Application
{
	private readonly IUiModeService _uiModeService;

	public App(IUiModeService uiModeService)
	{
		_uiModeService = uiModeService;
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "Mesh" };

		// Restore the last size and position, or open at the 1470 x 350 default, centered.
		WindowGeometry.Apply(window);

		// Persist geometry when the window loses focus and when it is torn down, so we reopen
		// where the user left it. (Deactivated fires often but a Preferences write is cheap.)
		window.Deactivated += (_, _) => WindowGeometry.Save(window);
		window.Destroying += (_, _) => WindowGeometry.Save(window);

		// Keep the UiModeService aware of the window size so Auto mode can resolve correctly.
		window.SizeChanged += (_, _) => _uiModeService.UpdateWindowSize(window.Width, window.Height);

		// WindowGeometry.Apply has already set Width/Height, so the initial dimensions are valid.
		_uiModeService.UpdateWindowSize(window.Width, window.Height);

		return window;
	}
}
