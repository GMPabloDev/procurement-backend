namespace ProcureToPay.Application.Abstractions.Files;

public interface IFileStorage
{
    Task UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default);

    Uri GenerateTemporaryDownloadUrl(string objectKey);
}
