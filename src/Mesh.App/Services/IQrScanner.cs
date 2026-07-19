namespace Mesh.App.Services;

public interface IQrScanner
{
    bool IsSupported { get; }
    Task<string?> ScanAsync(CancellationToken ct = default);
}
