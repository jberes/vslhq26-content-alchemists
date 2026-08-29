using Azure.Identity;
using Azure.Storage.Blobs;
using Castmill.Api.Services.Blob;
using Microsoft.AspNetCore.DataProtection;

namespace Castmill.Api.Services.DataProtection;

public sealed class DataProtectionStorageOptions
{
    public const string SectionName = "DataProtection";

    public string? BlobPath { get; set; }
}

public static class DataProtectionServiceCollectionExtensions
{
    internal const string ApplicationName = "Castmill";

    public static IServiceCollection AddCastmillDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction)
    {
        services.Configure<DataProtectionStorageOptions>(
            configuration.GetSection(DataProtectionStorageOptions.SectionName));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName(ApplicationName);

        if (!isProduction)
        {
            return services;
        }

        var accountName = RequireProductionSetting(
            configuration,
            $"{StorageOptions.SectionName}:AccountName");
        var privateContainer = RequireProductionSetting(
            configuration,
            $"{StorageOptions.SectionName}:PrivateContainer");
        var blobPath = RequireProductionSetting(
            configuration,
            $"{DataProtectionStorageOptions.SectionName}:BlobPath");

        var credentialOptions = new DefaultAzureCredentialOptions();
        if (configuration["AZURE_CLIENT_ID"] is { Length: > 0 } managedIdentityClientId)
        {
            credentialOptions.ManagedIdentityClientId = managedIdentityClientId;
        }

        var blobService = new BlobServiceClient(
            new Uri($"https://{accountName}.blob.core.windows.net"),
            new DefaultAzureCredential(credentialOptions));
        var keyBlob = blobService
            .GetBlobContainerClient(privateContainer)
            .GetBlobClient(blobPath);

        dataProtection.PersistKeysToAzureBlobStorage(keyBlob);
        return services;
    }

    private static string RequireProductionSetting(
        IConfiguration configuration,
        string key)
    {
        var value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"Production Data Protection requires '{key}'. Configure the existing private "
            + "Azure Blob container and deterministic key-ring blob path; Castmill refuses "
            + "to use ephemeral App Service storage for external-auth correlation cookies.");
    }
}