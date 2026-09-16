namespace ProcureToPay.Application.Abstractions.Files;

public interface IFileStorage
{
    Task UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default);

    Uri GenerateTemporaryDownloadUrl(string objectKey);

    /// <summary>
    /// Read-only probe that the configured object storage is actually reachable. Readiness uses it so
    /// an inaccessible bucket degrades instead of reporting a healthy module (SPEC 09 REQ-12).
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
