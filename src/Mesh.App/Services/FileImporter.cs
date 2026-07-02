using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Result of importing a static document into knowledge.</summary>
public sealed record ImportResult(bool Ok, IReadOnlyList<KnowledgeItem> Items, string? Error);

/// <summary>Reads documents from disk as static knowledge (text, PDF, DOCX, XLSX, PPTX). A document is a fact source.</summary>
public sealed class FileImporter(DocumentExtractor extractor)
{
    public async Task<ImportResult> ImportAsync(string path, string visibility, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path))
                return new ImportResult(false, Array.Empty<KnowledgeItem>(), "File not found.");

            if (!DocumentExtractor.IsSupported(path))
                return new ImportResult(false, Array.Empty<KnowledgeItem>(),
                    $"Unsupported file type '{Path.GetExtension(path)}'. Supported: text, PDF, DOCX, XLSX, PPTX.");

            var content = await extractor.ExtractFileAsync(path, ct: ct);
            if (string.IsNullOrWhiteSpace(content))
                return new ImportResult(false, Array.Empty<KnowledgeItem>(), "No readable text found in that file.");

            var item = new KnowledgeItem
            {
                Title = Path.GetFileName(path),
                Content = content,
                Visibility = visibility,
                Source = KnowledgeSource.File,
                SourceRef = path
            };
            return new ImportResult(true, new[] { item }, null);
        }
        catch (Exception ex)
        {
            return new ImportResult(false, Array.Empty<KnowledgeItem>(), ex.Message);
        }
    }
}
