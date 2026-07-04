namespace Mesh.App.Services;

/// <summary>
/// Sink that model providers report token usage to after each upstream call. It folds the counts
/// into the active identity's running total via <see cref="AppState.AddTokens"/>, which resets to
/// zero whenever the selected model changes (the counter is only meaningful per model). Tokens are
/// the primary cost currency, so this drives the live counter shown in the UI.
/// </summary>
public sealed class TokenMeter(AppState state)
{
    /// <summary>Records usage for the currently selected model. Zero or negative counts are ignored.</summary>
    public void Record(long promptTokens, long completionTokens)
    {
        if (promptTokens <= 0 && completionTokens <= 0) return;
        state.AddTokens(state.CurrentModelKey(),
            Math.Max(0, promptTokens), Math.Max(0, completionTokens));
    }
}
