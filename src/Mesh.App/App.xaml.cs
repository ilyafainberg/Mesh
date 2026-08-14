using Mesh.App.Services;

namespace Mesh.App;

public partial class App : Application
{
	private readonly IUiModeService _uiModeService;
	private readonly MeshClient _meshClient;
	private readonly AppState _state;

	public App(IUiModeService uiModeService, MeshClient meshClient, AppState state)
	{
		_uiModeService = uiModeService;
		_meshClient = meshClient;
		_state = state;
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "Mesh" };
		window.Activated += (_, _) => _meshClient.ResumeTransport();
		window.Stopped += async (_, _) => await FlushPersistenceOnSuspendAsync();

		var hasDesktopWindowGeometry = (OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst())
			&& !MeshProcessContext.IsHeadless;
		if (hasDesktopWindowGeometry)
		{
			// Restore and persist resizable desktop window geometry. Mobile platforms own their
			// window dimensions and must not receive desktop width, height, or position values.
			WindowGeometry.Apply(window);
			window.Deactivated += (_, _) => WindowGeometry.Save(window);
			window.Destroying += (_, _) => WindowGeometry.Save(window);
		}

		// Keep the UiModeService aware of the window size so Auto mode can resolve correctly.
		window.SizeChanged += (_, _) => _uiModeService.UpdateWindowSize(window.Width, window.Height);

		// Mobile starts in the platform-derived Phone mode until its first real SizeChanged event.
		if (hasDesktopWindowGeometry)
			_uiModeService.UpdateWindowSize(window.Width, window.Height);
		else
			_uiModeService.UpdateWindowSize(0, 0);

		return window;
	}

	private async Task FlushPersistenceOnSuspendAsync()
	{
		try
		{
			await _state.FlushPersistenceAsync();
		}
		catch (Exception ex)
		{
			RuntimeDiagnostics.Current?.MarkLifecycle("persistence-flush-failed", ex.Message);
		}
	}
}
