using System.Globalization;

namespace Mesh.App.Services;

/// <summary>
/// Application-wide formatting for token amounts. Tokens are stored as raw counts (long); the UI
/// displays them in MTokens (millions of tokens) to one decimal place, e.g. 0.1 MTokens = 100,000
/// tokens, 1.0 MTokens = 1,000,000. Budget inputs are entered in MTokens and converted back to raw
/// token counts on save.
/// </summary>
public static class TokenFormat
{
    private const double Million = 1_000_000.0;

    /// <summary>Raw token count as an MTokens number to one decimal (no unit suffix), e.g. "0.1".</summary>
    public static double ToMTokensValue(long tokens) => Math.Round(tokens / Million, 1);

    /// <summary>Raw token count as a labeled MTokens string, e.g. "0.1 MTokens".</summary>
    public static string ToMTokens(long tokens)
        => ToMTokensValue(tokens).ToString("0.0", CultureInfo.InvariantCulture) + " MTokens";

    /// <summary>MTokens (entered by the user) back to a raw token count. Negative inputs clamp to 0.</summary>
    public static long FromMTokens(double mtokens) => mtokens <= 0 ? 0 : (long)Math.Round(mtokens * Million);

    /// <summary>"spent / total MTokens" for a budget display, or "spent / unlimited" when total is 0.</summary>
    public static string SpentOfTotal(long spent, long total)
        => total > 0
            ? $"{ToMTokensValue(spent).ToString("0.0", CultureInfo.InvariantCulture)} / {ToMTokensValue(total).ToString("0.0", CultureInfo.InvariantCulture)} MTokens"
            : $"{ToMTokensValue(spent).ToString("0.0", CultureInfo.InvariantCulture)} MTokens / unlimited";
}
