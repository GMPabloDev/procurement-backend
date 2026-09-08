using Microsoft.Extensions.Configuration;

namespace ProcureToPay.Infrastructure.Storage.S3;

internal sealed class S3StorageOptions
{
    public const string ConfigurationSection = "Storage:S3";

    public string BucketName { get; init; } = string.Empty;

    public long MaxUploadSizeBytes { get; init; } = 10 * 1024 * 1024;

    public string[] AllowedExtensions { get; init; } = [".pdf", ".png", ".jpg", ".jpeg"];

    public int TemporaryUrlLifetimeMinutes { get; init; } = 15;

    public static S3StorageOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(ConfigurationSection);
        var extensions = section.GetSection("AllowedExtensions")
            .GetChildren()
            .Select(item => item.Value)
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Cast<string>()
            .ToArray();

        return new S3StorageOptions
        {
            BucketName = section["BucketName"] ?? string.Empty,
            MaxUploadSizeBytes = long.TryParse(section["MaxUploadSizeBytes"], out var maxUploadSizeBytes)
                ? maxUploadSizeBytes
                : 10 * 1024 * 1024,
            AllowedExtensions = extensions.Length > 0
                ? extensions
                : [".pdf", ".png", ".jpg", ".jpeg"],
            TemporaryUrlLifetimeMinutes = int.TryParse(section["TemporaryUrlLifetimeMinutes"], out var lifetimeMinutes)
                ? lifetimeMinutes
                : 15
        };
    }
}
