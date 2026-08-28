using Microsoft.JSInterop;
using System.Text;

namespace Mesh.App.Services;

public sealed class WidgetDiagnosticsBridge(RuntimeDiagnostics diagnostics)
{
    private const int MaxStageChars = 64;
    private const int MaxDetailChars = 768;
    private const string TruncatedSuffix = " [truncated]";

    [JSInvokable]
    public void RecordStage(string stage, string? detail)
    {
        var normalizedStage = NormalizeStage(stage);
        if (normalizedStage.Length == 0) return;

        var normalizedDetail = NormalizeDetail(detail);
        diagnostics.RecordEvent(
            "widget-render",
            normalizedDetail.Length == 0
                ? $"stage={normalizedStage}"
                : $"stage={normalizedStage}; {normalizedDetail}");
    }

    internal static string NormalizeStage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder(Math.Min(value.Length, MaxStageChars));
        var separatorPending = false;
        foreach (var character in value.Trim())
        {
            var isAsciiLetter = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
            if (isAsciiLetter || character is >= '0' and <= '9')
            {
                if (separatorPending && result.Length > 0 && result[^1] != '-')
                {
                    if (result.Length >= MaxStageChars - 1) break;
                    result.Append('-');
                }
                if (result.Length >= MaxStageChars) break;
                result.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = result.Length > 0;
            }

            if (result.Length >= MaxStageChars) break;
        }
        return result.ToString().TrimEnd('-');
    }

    internal static string NormalizeDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder(Math.Min(value.Length, MaxDetailChars));
        var whitespacePending = false;
        var truncated = false;
        foreach (var character in value.Trim())
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                whitespacePending = result.Length > 0;
                continue;
            }

            if (whitespacePending && result.Length < MaxDetailChars) result.Append(' ');
            whitespacePending = false;
            if (result.Length >= MaxDetailChars)
            {
                truncated = true;
                break;
            }
            result.Append(character);
        }

        if (truncated && result.Length > TruncatedSuffix.Length)
        {
            result.Length = MaxDetailChars - TruncatedSuffix.Length;
            result.Append(TruncatedSuffix);
        }
        return result.ToString().Trim();
    }
}
