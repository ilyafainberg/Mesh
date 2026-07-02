using System.Text;
using DocumentFormat.OpenXml.Packaging;
using SS = DocumentFormat.OpenXml.Spreadsheet;
using DW = DocumentFormat.OpenXml.Wordprocessing;
using DD = DocumentFormat.OpenXml.Drawing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Mesh.App.Services;

/// <summary>
/// Extracts plain text from documents (PDF, DOCX, XLSX, PPTX) and plain-text formats.
/// Pure-managed (PdfPig + OpenXml), no native dependencies, so it works anywhere MAUI does.
/// Used by both local file import and the cloud file connectors (OneDrive/SharePoint/Drive).
/// </summary>
public sealed class DocumentExtractor
{
    /// <summary>Extensions we can turn into text. Kept in sync with <see cref="Extract"/>.</summary>
    public static readonly string[] SupportedExtensions =
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".log", ".yml", ".yaml",
        ".pdf", ".docx", ".xlsx", ".pptx"
    };

    public static bool IsSupported(string fileName)
        => SupportedExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Extracts text from raw bytes, choosing the parser by file extension.</summary>
    public string Extract(byte[] bytes, string fileName, int maxChars = 40000)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var text = ext switch
        {
            ".pdf" => ExtractPdf(bytes),
            ".docx" => ExtractDocx(bytes),
            ".xlsx" => ExtractXlsx(bytes),
            ".pptx" => ExtractPptx(bytes),
            _ => ExtractPlain(bytes, ext),
        };
        text = text.Trim();
        return text.Length > maxChars ? text[..maxChars] + "\n…(truncated)" : text;
    }

    public async Task<string> ExtractFileAsync(string path, int maxChars = 40000, CancellationToken ct = default)
        => Extract(await File.ReadAllBytesAsync(path, ct), path, maxChars);

    private static string ExtractPlain(byte[] bytes, string ext)
    {
        var raw = Encoding.UTF8.GetString(bytes);
        if (ext is ".html" or ".htm")
        {
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "<script.*?</script>", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "<style.*?</style>", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]+>", " ");
            raw = System.Net.WebUtility.HtmlDecode(raw);
        }
        return raw;
    }

    private static string ExtractPdf(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "";
        var sb = new StringBuilder();
        foreach (var para in body.Descendants<DW.Paragraph>())
        {
            var line = para.InnerText;
            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string ExtractXlsx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(ms, false);
        var wbPart = doc.WorkbookPart;
        if (wbPart is null) return "";
        var shared = wbPart.SharedStringTablePart?.SharedStringTable;
        var sb = new StringBuilder();

        var sheets = wbPart.Workbook?.Sheets?.Elements<SS.Sheet>() ?? Enumerable.Empty<SS.Sheet>();
        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value is null) continue;
            if (wbPart.GetPartById(sheet.Id!.Value!) is not WorksheetPart wsPart) continue;
            sb.AppendLine($"# Sheet: {sheet.Name}");
            foreach (var row in wsPart.Worksheet?.Descendants<SS.Row>() ?? Enumerable.Empty<SS.Row>())
            {
                var cells = row.Elements<SS.Cell>().Select(c => CellText(c, shared));
                var line = string.Join("\t", cells).TrimEnd();
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string CellText(SS.Cell cell, SS.SharedStringTable? shared)
    {
        var val = cell.CellValue?.InnerText ?? cell.InnerText;
        if (cell.DataType?.Value == SS.CellValues.SharedString && shared is not null
            && int.TryParse(val, out var idx) && idx >= 0 && idx < shared.ChildElements.Count)
            return shared.ChildElements[idx].InnerText;
        return val;
    }

    private static string ExtractPptx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = PresentationDocument.Open(ms, false);
        var presPart = doc.PresentationPart;
        if (presPart is null) return "";
        var sb = new StringBuilder();
        var n = 0;
        foreach (var slidePart in presPart.SlideParts)
        {
            n++;
            sb.AppendLine($"# Slide {n}");
            foreach (var t in slidePart.Slide?.Descendants<DD.Text>() ?? Enumerable.Empty<DD.Text>())
                if (!string.IsNullOrWhiteSpace(t.Text)) sb.AppendLine(t.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
