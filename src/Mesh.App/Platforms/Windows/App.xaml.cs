using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Mesh.App.Services;

namespace Mesh.App.WinUI;

/// <summary>Provides the WinUI host for the MAUI application.</summary>
public partial class App : MauiWinUIApplication
{
    // The installer probes this process-wide name before replacing binaries. It is intentionally
    // not acquired: per-handle mutexes enforce runtime identity ownership.
    private static Mutex? installerDetectionMutex;

    public App()
    {
        try { installerDetectionMutex ??= new Mutex(false, "MeshApp.SingleInstance"); }
        catch { }
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MeshDesktopLaunchDecision decision;
        try
        {
            decision = await MeshDesktopLaunchBootstrap.PrepareAsync(
                Environment.GetCommandLineArgs(),
                StoragePaths.Root,
                StoragePaths.DataDir,
                Console.Out);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            decision = new MeshDesktopLaunchDecision(false, 1);
        }

        if (!decision.ContinueLaunching)
        {
            Environment.ExitCode = decision.ExitCode;
            Microsoft.UI.Xaml.Application.Current.Exit();
            return;
        }

        try
        {
            DispatchActivation(AppInstance.GetCurrent().GetActivatedEventArgs());
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("windows-activation", ex);
        }

        base.OnLaunched(args);
    }

    private static void DispatchActivation(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Protocol
            && args.Data is IProtocolActivatedEventArgs protocol
            && protocol.Uri is not null)
        {
            DeepLinkDispatch.Dispatch(protocol.Uri.ToString());
            return;
        }
        if (args.Kind == ExtendedActivationKind.Launch
            && args.Data is ILaunchActivatedEventArgs launch
            && !string.IsNullOrWhiteSpace(launch.Arguments))
            DispatchFromArgs(UiModeParser.SplitWindowsArgs(launch.Arguments));
    }

    internal static void DispatchFromArgs(IReadOnlyList<string> arguments)
    {
        UiModeActivationBridge.ApplyCommandLine(arguments);
        foreach (var argument in arguments)
        {
            var value = argument.Trim().Trim('"');
            if (!value.StartsWith("mesh://", StringComparison.OrdinalIgnoreCase)) continue;
            DeepLinkDispatch.Dispatch(value);
            return;
        }
    }
}
