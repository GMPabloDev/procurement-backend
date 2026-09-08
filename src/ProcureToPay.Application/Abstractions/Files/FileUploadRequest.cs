namespace ProcureToPay.Application.Abstractions.Files;

public sealed class FileUploadRequest
{
    public required string ObjectKey { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required Stream Content { get; init; }

    public required long Length { get; init; }
}
