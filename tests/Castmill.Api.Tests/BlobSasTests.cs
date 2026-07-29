using System.Security.Cryptography;
using Azure.Storage.Sas;
using Castmill.Api.Services.Blob;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

/// <summary>
/// Shared-key SAS generation is pure crypto — no network — so the G2 scoping
/// rules (single blob, single op, capped expiry) are provable offline.
/// </summary>
public sealed class BlobSasTests
{
    internal static string FakeConnectionString { get; } =
        "DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey=" +
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) +
        ";EndpointSuffix=core.windows.net";

    private static BlobSasService CreateService(int maxMinutes = 60) =>
        new(Options.Create(new StorageOptions
        {
            ConnectionString = FakeConnectionString,
            PrivateContainer = "private",
            DefaultSasMinutes = 10,
            MaxSasMinutes = maxMinutes,
        }));

    [Fact]
    public async Task Upload_sas_is_single_blob_write_only()
    {
        var url = await CreateService().MintAsync(
            "tenants/t1/assets/a1/file.mp4", BlobSasPermissions.Create | BlobSasPermissions.Write, null,
            TestContext.Current.CancellationToken);

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("b", query["sr"]);   // single blob, never a container grant
        Assert.Equal("cw", query["sp"]);  // create+write only — no read, no delete
        Assert.DoesNotContain("fakeaccount", query["sig"], StringComparison.Ordinal);
        Assert.EndsWith("/private/tenants/t1/assets/a1/file.mp4", url.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_sas_is_read_only()
    {
        var url = await CreateService().MintAsync(
            "tenants/t1/assets/a1/file.mp4", BlobSasPermissions.Read, null,
            TestContext.Current.CancellationToken);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("r", query["sp"]);
    }

    [Fact]
    public async Task Requested_expiry_is_clamped_to_the_cap()
    {
        var url = await CreateService(maxMinutes: 60).MintAsync(
            "tenants/t1/assets/a1/file.mp4", BlobSasPermissions.Read, minutes: 9999,
            TestContext.Current.CancellationToken);

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        var expiresOn = DateTimeOffset.Parse(query["se"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(expiresOn <= DateTimeOffset.UtcNow.AddMinutes(61),
            $"SAS expiry {expiresOn:O} exceeds the 60-minute cap.");
    }

    [Fact]
    public void Unconfigured_service_reports_not_configured()
    {
        var service = new BlobSasService(Options.Create(new StorageOptions()));
        Assert.False(service.IsConfigured);
    }
}
