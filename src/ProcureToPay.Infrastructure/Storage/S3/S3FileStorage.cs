using Amazon.S3;
using Amazon.S3.Model;
using ProcureToPay.Application.Abstractions.Files;

namespace ProcureToPay.Infrastructure.Storage.S3;

internal sealed class S3FileStorage(IAmazonS3 s3Client, S3StorageOptions options) : IFileStorage
{
    private readonly HashSet<string> _allowedExtensions = options.AllowedExtensions
        .Select(NormalizeExtension)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ValidateUpload(request);

        var putRequest = new PutObjectRequest
        {
            BucketName = GetBucketName(),
            Key = request.ObjectKey,
            InputStream = request.Content,
            ContentType = request.ContentType
        };

        await s3Client.PutObjectAsync(putRequest, cancellationToken);
    }

    public Uri GenerateTemporaryDownloadUrl(string objectKey, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException("An object key is required.", nameof(objectKey));
        }

        if (options.TemporaryUrlLifetimeMinutes is <= 0 or > 60)
        {
            throw new InvalidOperationException(
                "Storage:S3:TemporaryUrlLifetimeMinutes must be between 1 and 60.");
        }

        var minutes = lifetime?.TotalMinutes ?? options.TemporaryUrlLifetimeMinutes;
        if (minutes is <= 0 or > 60)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime), "A temporary download URL lives between 1 and 60 minutes.");
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = GetBucketName(),
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(minutes)
        };

        return new Uri(s3Client.GetPreSignedURL(request));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // A single-key listing is a real read-only round trip to the bucket: an unreachable or
            // forbidden bucket fails here instead of being reported as available.
            await s3Client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = GetBucketName(), MaxKeys = 1 },
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private void ValidateUpload(FileUploadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ObjectKey))
        {
            throw new ArgumentException("An object key is required.", nameof(request));
        }

        if (request.Content is null || !request.Content.CanRead)
        {
            throw new ArgumentException("A readable content stream is required.", nameof(request));
        }

        if (request.Length <= 0 || request.Length > options.MaxUploadSizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"File size must be between 1 byte and {options.MaxUploadSizeBytes} bytes.");
        }

        var extension = NormalizeExtension(Path.GetExtension(request.FileName));
        if (string.IsNullOrWhiteSpace(extension) || !_allowedExtensions.Contains(extension))
        {
            throw new ArgumentException("The file extension is not allowed.", nameof(request));
        }
    }

    private string GetBucketName()
    {
        return !string.IsNullOrWhiteSpace(options.BucketName)
            ? options.BucketName
            : throw new InvalidOperationException("Storage:S3:BucketName must be configured.");
    }

    private static string NormalizeExtension(string extension)
    {
        return extension.StartsWith('.') ? extension : $".{extension}";
    }
}
