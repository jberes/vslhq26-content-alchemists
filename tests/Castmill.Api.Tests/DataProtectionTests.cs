using Castmill.Api.Auth;
using Castmill.Api.Services.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

public sealed class DataProtectionTests
{
    [Theory]
    [InlineData("Storage:AccountName")]
    [InlineData("Storage:PrivateContainer")]
    [InlineData("DataProtection:BlobPath")]
    public void Production_refuses_missing_blob_key_ring_configuration(string missingKey)
    {
        var values = ProductionConfiguration();
        values.Remove(missingKey);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCastmillDataProtection(configuration, isProduction: true));

        Assert.Contains(missingKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("ephemeral App Service storage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_does_not_require_Azure_storage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCastmillDataProtection(
            new ConfigurationBuilder().Build(),
            isProduction: false);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public void Production_registers_Azure_Blob_key_repository_without_network_access()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCastmillDataProtection(
            new ConfigurationBuilder()
                .AddInMemoryCollection(ProductionConfiguration())
                .Build(),
            isProduction: true);

        using var provider = services.BuildServiceProvider();
        var keyManagement = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var dataProtection = provider
            .GetRequiredService<IOptions<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>>()
            .Value;

        Assert.NotNull(provider.GetRequiredService<IDataProtectionProvider>());
        Assert.NotNull(keyManagement.XmlRepository);
        Assert.Contains("AzureBlob", keyManagement.XmlRepository.GetType().Name, StringComparison.Ordinal);
        Assert.Equal("Castmill", dataProtection.ApplicationDiscriminator);
    }

    [Fact]
    public void Shared_key_ring_survives_service_provider_restart()
    {
        var keyDirectory = Directory.CreateTempSubdirectory("castmill-data-protection-");
        try
        {
            string protectedPayload;
            using (var firstProvider = BuildFileBackedProvider(keyDirectory))
            {
                protectedPayload = firstProvider
                    .GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector(ExternalAuthSchemes.Microsoft, "correlation-cookie")
                    .Protect("external-auth-state");
            }

            using var secondProvider = BuildFileBackedProvider(keyDirectory);
            var payload = secondProvider
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(ExternalAuthSchemes.Microsoft, "correlation-cookie")
                .Unprotect(protectedPayload);

            Assert.Equal("external-auth-state", payload);
        }
        finally
        {
            keyDirectory.Delete(recursive: true);
        }
    }

    private static ServiceProvider BuildFileBackedProvider(DirectoryInfo keyDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName(
                Castmill.Api.Services.DataProtection.DataProtectionServiceCollectionExtensions.ApplicationName)
            .PersistKeysToFileSystem(keyDirectory);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ProductionConfiguration() => new()
    {
        ["Storage:AccountName"] = "castmillstorage",
        ["Storage:PrivateContainer"] = "private",
        ["DataProtection:BlobPath"] = "system/data-protection/castmill-keyring.xml",
        ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000001",
    };
}