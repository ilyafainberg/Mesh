using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Owns the "save &amp; test the model" operation (which for On Device may install,
/// download and load a model, potentially minutes long). Because it's a singleton,
/// the work and its live status survive navigating away from the Settings page, and
/// any component can observe progress via <see cref="Changed"/>.
/// </summary>
public sealed class ModelSetupService(AppState state, AgentService agent)
{
    private CancellationTokenSource? cts;

    /// <summary>True while a save/test is in flight.</summary>
    public bool Running { get; private set; }
    /// <summary>Latest human-readable status line (progress or final result).</summary>
    public string? Status { get; private set; }
    /// <summary>Null while running; true/false once finished.</summary>
    public bool? Success { get; private set; }
    /// <summary>Set true once the user dismisses a finished result banner.</summary>
    public bool Dismissed { get; private set; }

    public event Action? Changed;

    /// <summary>Whether a status banner should currently be shown anywhere in the app.</summary>
    public bool HasBanner => Running || (Success is not null && !Dismissed);

    /// <summary>
    /// Applies the model config to the profile, then tests it in the background.
    /// Returns immediately; observe <see cref="Changed"/> for progress. If a run is
    /// already in flight it is cancelled and replaced.
    /// </summary>
    public void SaveAndTest(Action<MeshProfile> applyConfig)
    {
        cts?.Cancel();
        var localCts = cts = new CancellationTokenSource();

        state.Mutate(applyConfig);

        Running = true;
        Dismissed = false;
        Success = null;
        Status = "Starting…";
        Notify();

        _ = Task.Run(() => RunAsync(localCts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var progress = new Progress<string>(s =>
            {
                if (ct.IsCancellationRequested) return;
                Status = s;
                Notify();
            });

            var (ok, message) = await agent.TestModelAsync(state.Profile.Model, progress, ct);
            if (ct.IsCancellationRequested) return;

            Success = ok;
            Status = ok ? "Model is working." : "Model test failed: " + message;

            // Persist the concrete model/endpoint that On Device resolved.
            if (ok && state.Profile.Model.Provider == ModelProvider.FoundryLocal)
                state.Save();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Success = false;
            Status = "Model test failed: " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                Running = false;
                Notify();
            }
        }
    }

    public void Dismiss()
    {
        Dismissed = true;
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}
