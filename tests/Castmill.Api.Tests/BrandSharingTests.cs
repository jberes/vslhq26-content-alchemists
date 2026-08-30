using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Api.Endpoints;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class BrandSharingTests(CastmillApiFactory factory)
{
    private async Task<(HttpClient Client, string Email)> RegisterAsync(
        string displayName,
        WebApplicationFactory<Program>? app = null)
    {
        var email = $"brand-share-{Guid.NewGuid():N}@example.com";
        var client = (app ?? factory).CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "correct-horse-battery-staple", displayName));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, email);
    }

    [Fact]
    public async Task Owner_can_share_full_brand_access_with_an_existing_user_and_revoke_it()
    {
        var (owner, _) = await RegisterAsync("Brand Owner");
        var (collaborator, collaboratorEmail) = await RegisterAsync("Brand Collaborator");
        var (stranger, _) = await RegisterAsync("Unrelated User");

        var createBrand = await owner.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("Shared Brand", new BrandStyleCard(Voice: "Original voice")));
        createBrand.EnsureSuccessStatusCode();
        var brand = (await createBrand.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        Assert.True(brand.IsOwner);

        var ownerAsset = await (await owner.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("owner-logo.png", "image/png", 100))).Content
            .ReadFromJsonAsync<AssetResponse>();
        var ownerLink = await owner.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/assets",
            new BrandAssetLinkRequest(ownerAsset!.Id, "logo", "Owner logo"));
        Assert.True(ownerLink.IsSuccessStatusCode, await ownerLink.Content.ReadAsStringAsync());

        var ownerCampaign = await (await owner.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Owner campaign", null, brand.Id))).Content
            .ReadFromJsonAsync<CampaignResponse>();

        var unknown = await owner.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/collaborators",
            new BrandCollaboratorRequest($"missing-{Guid.NewGuid():N}@example.com"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("not available for sharing", await unknown.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        var add = await owner.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/collaborators",
            new BrandCollaboratorRequest(collaboratorEmail.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var share = (await add.Content.ReadFromJsonAsync<BrandCollaboratorResponse>())!;
        Assert.Equal(collaboratorEmail, share.Email, ignoreCase: true);
        Assert.Equal("Brand Collaborator", share.DisplayName);

        var ownerShares = await owner.GetFromJsonAsync<List<BrandCollaboratorResponse>>(
            $"/api/v1/brands/{brand.Id}/collaborators");
        Assert.Equal(share.Id, Assert.Single(ownerShares!).Id);

        var collaboratorBrands = await collaborator.GetFromJsonAsync<List<BrandProfileDetailResponse>>(
            "/api/v1/brands");
        var sharedBrand = Assert.Single(collaboratorBrands!, item => item.Id == brand.Id);
        Assert.False(sharedBrand.IsOwner);

        var update = await collaborator.PutAsJsonAsync($"/api/v1/brands/{brand.Id}",
            new BrandProfileUpsertRequest("Shared Brand Updated",
                new BrandStyleCard(Voice: "Collaborative voice")));
        update.EnsureSuccessStatusCode();
        Assert.False((await update.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!.IsOwner);

        var template = await collaborator.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/templates",
            new BrandTemplateRequest("blog", "Shared brief", "Use the shared brief.", IsDefault: true));
        template.EnsureSuccessStatusCode();

        var collaboratorKit = await collaborator.GetFromJsonAsync<List<BrandAssetResponse>>(
            $"/api/v1/brands/{brand.Id}/assets");
        Assert.Equal(ownerAsset.Id, Assert.Single(collaboratorKit!).AssetId);

        var collaboratorAsset = await (await collaborator.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("collaborator-product.png", "image/png", 100))).Content
            .ReadFromJsonAsync<AssetResponse>();
        (await collaborator.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/assets",
            new BrandAssetLinkRequest(collaboratorAsset!.Id, "product", "Shared product")))
            .EnsureSuccessStatusCode();

        var deleteLinkedAsset = await collaborator.DeleteAsync($"/api/v1/assets/{collaboratorAsset.Id}");
        Assert.Equal(HttpStatusCode.Conflict, deleteLinkedAsset.StatusCode);
        Assert.Equal(2, (await owner.GetFromJsonAsync<List<BrandAssetResponse>>(
            $"/api/v1/brands/{brand.Id}/assets"))!.Count);

        var collaboratorCampaignResponse = await collaborator.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Collaborator campaign", null, brand.Id));
        collaboratorCampaignResponse.EnsureSuccessStatusCode();
        var collaboratorCampaign =
            (await collaboratorCampaignResponse.Content.ReadFromJsonAsync<CampaignResponse>())!;
        var preview = await collaborator.GetAsync(
            $"/api/v1/campaigns/{collaboratorCampaign.Id}/preview");
        preview.EnsureSuccessStatusCode();
        Assert.Contains("Shared Brand Updated", await preview.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/brands/{brand.Id}/collaborators")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.DeleteAsync($"/api/v1/brands/{brand.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/v1/brands/{brand.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await stranger.PostAsJsonAsync("/api/v1/campaigns",
                new CampaignCreateRequest("Unauthorized campaign", null, brand.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/campaigns/{ownerCampaign!.Id}")).StatusCode);

        var revoke = await owner.DeleteAsync(
            $"/api/v1/brands/{brand.Id}/collaborators/{share.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/brands/{brand.Id}")).StatusCode);

        var detached = await collaborator.GetFromJsonAsync<CampaignResponse>(
            $"/api/v1/campaigns/{collaboratorCampaign.Id}");
        Assert.Null(detached!.BrandId);
        Assert.Equal(HttpStatusCode.NoContent,
            (await collaborator.DeleteAsync($"/api/v1/assets/{collaboratorAsset.Id}")).StatusCode);
        Assert.Single((await owner.GetFromJsonAsync<List<BrandAssetResponse>>(
            $"/api/v1/brands/{brand.Id}/assets"))!);

        var ownerView = await owner.GetFromJsonAsync<BrandProfileDetailResponse>(
            $"/api/v1/brands/{brand.Id}");
        Assert.True(ownerView!.IsOwner);
        Assert.Equal("Shared Brand Updated", ownerView.Name);
    }

    [Fact]
    public async Task Shared_brand_voice_and_template_reach_collaborator_generation()
    {
        var capture = new BrandDomainTests.CapturingFoundryFactory();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(_ => capture))));
        var (owner, _) = await RegisterAsync("Generation Owner", app);
        var (collaborator, collaboratorEmail) = await RegisterAsync("Generation Collaborator", app);

        var brandResponse = await owner.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("Shared Generation Brand",
                new BrandStyleCard(Voice: "SHARED-VOICE-MARKER")));
        var brand = (await brandResponse.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        (await owner.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/templates",
            new BrandTemplateRequest(
                "newsletter", "Shared strategy", "SHARED-TEMPLATE-MARKER", IsDefault: true)))
            .EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/collaborators",
            new BrandCollaboratorRequest(collaboratorEmail))).EnsureSuccessStatusCode();

        var campaignResponse = await collaborator.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Shared generation", null, brand.Id));
        var campaign = (await campaignResponse.Content.ReadFromJsonAsync<CampaignResponse>())!;
        var ingest = await collaborator.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/transcripts",
            new { text = "The release reduced deployment time by half.", source = "test" });
        ingest.EnsureSuccessStatusCode();
        var transcript = (await ingest.Content.ReadFromJsonAsync<IngestResponse>())!;

        var generate = await collaborator.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/newsletter",
            new { transcriptArtifactId = transcript.TranscriptArtifactId });
        generate.EnsureSuccessStatusCode();

        var prompt = Assert.Single(capture.Prompts,
            value => value.Contains("newsletter edition", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("SHARED-VOICE-MARKER", prompt, StringComparison.Ordinal);
        Assert.Contains("SHARED-TEMPLATE-MARKER", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_asset_link_and_revocation_leave_no_grant_or_orphaned_link()
    {
        var (owner, _) = await RegisterAsync("Race Owner");
        var (collaborator, collaboratorEmail) = await RegisterAsync("Race Collaborator");
        var brandResponse = await owner.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("Race Brand", null));
        var brand = (await brandResponse.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        var shareResponse = await owner.PostAsJsonAsync(
            $"/api/v1/brands/{brand.Id}/collaborators",
            new BrandCollaboratorRequest(collaboratorEmail));
        var share = (await shareResponse.Content.ReadFromJsonAsync<BrandCollaboratorResponse>())!;
        var asset = await (await collaborator.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("race-product.png", "image/png", 100))).Content
            .ReadFromJsonAsync<AssetResponse>();

        var linkTask = collaborator.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/assets",
            new BrandAssetLinkRequest(asset!.Id, "product", "Race product"));
        var revokeTask = owner.DeleteAsync(
            $"/api/v1/brands/{brand.Id}/collaborators/{share.Id}");
        await Task.WhenAll(linkTask, revokeTask);
        var linkResponse = await linkTask;
        var revokeResponse = await revokeTask;

        Assert.Contains(linkResponse.StatusCode,
            new[] { HttpStatusCode.Created, HttpStatusCode.NotFound });
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<BrandAssetResponse>>(
            $"/api/v1/brands/{brand.Id}/assets"))!);
        Assert.Empty((await owner.GetFromJsonAsync<List<BrandCollaboratorResponse>>(
            $"/api/v1/brands/{brand.Id}/collaborators"))!);
        Assert.Equal(HttpStatusCode.NoContent,
            (await collaborator.DeleteAsync($"/api/v1/assets/{asset.Id}")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_campaign_attach_and_revocation_cannot_restore_brand_access()
    {
        var (owner, _) = await RegisterAsync("Campaign Race Owner");
        var (collaborator, collaboratorEmail) = await RegisterAsync("Campaign Race Collaborator");
        var brandResponse = await owner.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("Campaign Race Brand", null));
        var brand = (await brandResponse.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        var shareResponse = await owner.PostAsJsonAsync(
            $"/api/v1/brands/{brand.Id}/collaborators",
            new BrandCollaboratorRequest(collaboratorEmail));
        var share = (await shareResponse.Content.ReadFromJsonAsync<BrandCollaboratorResponse>())!;

        var createTask = collaborator.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Racing campaign", null, brand.Id));
        var revokeTask = owner.DeleteAsync(
            $"/api/v1/brands/{brand.Id}/collaborators/{share.Id}");
        await Task.WhenAll(createTask, revokeTask);
        var createResponse = await createTask;
        var revokeResponse = await revokeTask;

        Assert.Contains(createResponse.StatusCode,
            new[] { HttpStatusCode.Created, HttpStatusCode.BadRequest });
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        if (createResponse.StatusCode == HttpStatusCode.Created)
        {
            var campaign = (await createResponse.Content.ReadFromJsonAsync<CampaignResponse>())!;
            var reloaded = await collaborator.GetFromJsonAsync<CampaignResponse>(
                $"/api/v1/campaigns/{campaign.Id}");
            Assert.Null(reloaded!.BrandId);
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/brands/{brand.Id}")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_campaign_update_and_revocation_cannot_restore_brand_access()
    {
        var (owner, _) = await RegisterAsync("Campaign Update Owner");
        var (collaborator, collaboratorEmail) = await RegisterAsync("Campaign Update Collaborator");
        var brandResponse = await owner.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("Campaign Update Brand", null));
        var brand = (await brandResponse.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        var shareResponse = await owner.PostAsJsonAsync(
            $"/api/v1/brands/{brand.Id}/collaborators",
            new BrandCollaboratorRequest(collaboratorEmail));
        var share = (await shareResponse.Content.ReadFromJsonAsync<BrandCollaboratorResponse>())!;
        var campaignResponse = await collaborator.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Campaign to update", null));
        var campaign = (await campaignResponse.Content.ReadFromJsonAsync<CampaignResponse>())!;

        var updateTask = collaborator.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}",
            new CampaignUpdateRequest(campaign.Name, campaign.Brief, brand.Id));
        var revokeTask = owner.DeleteAsync(
            $"/api/v1/brands/{brand.Id}/collaborators/{share.Id}");
        await Task.WhenAll(updateTask, revokeTask);
        var updateResponse = await updateTask;
        var revokeResponse = await revokeTask;

        Assert.Contains(updateResponse.StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        var reloaded = await collaborator.GetFromJsonAsync<CampaignResponse>(
            $"/api/v1/campaigns/{campaign.Id}");
        Assert.Null(reloaded!.BrandId);
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/brands/{brand.Id}")).StatusCode);
    }

    [Fact]
    public async Task Shared_asset_read_mint_and_revocation_are_serialized()
    {
        var blobs = new BlockingMintBlobStore();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IBlobSasService>(blobs))));
        var (owner, _) = await RegisterAsync("SAS Owner", app);
        var (collaborator, collaboratorEmail) = await RegisterAsync("SAS Collaborator", app);
        var brandResponse = await owner.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("SAS Brand", null));
        var brand = (await brandResponse.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        var asset = await (await owner.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("shared.png", "image/png", 100))).Content
            .ReadFromJsonAsync<AssetResponse>();
        (await owner.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/assets",
            new BrandAssetLinkRequest(asset!.Id, "logo", "Shared logo"))).EnsureSuccessStatusCode();
        var shareResponse = await owner.PostAsJsonAsync(
            $"/api/v1/brands/{brand.Id}/collaborators",
            new BrandCollaboratorRequest(collaboratorEmail));
        var share = (await shareResponse.Content.ReadFromJsonAsync<BrandCollaboratorResponse>())!;

        var readTask = collaborator.GetAsync($"/api/v1/blob/assets/{asset.Id}/read-sas");
        await blobs.MintStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revokeTask = owner.DeleteAsync(
            $"/api/v1/brands/{brand.Id}/collaborators/{share.Id}");
        Assert.NotSame(revokeTask, await Task.WhenAny(revokeTask, Task.Delay(250)));

        blobs.AllowMint.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await readTask).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await revokeTask).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/blob/assets/{asset.Id}/read-sas")).StatusCode);
    }

    [Fact]
    public async Task Brand_owner_can_read_and_preview_a_collaborator_contributed_asset()
    {
        var blobs = new ImmediateBlobStore();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IBlobSasService>(blobs))));
        var (owner, _) = await RegisterAsync("Contributed Asset Owner", app);
        var (collaborator, collaboratorEmail) = await RegisterAsync("Asset Contributor", app);
        var brandResponse = await owner.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("Contributed Asset Brand", null));
        var brand = (await brandResponse.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        (await owner.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/collaborators",
            new BrandCollaboratorRequest(collaboratorEmail))).EnsureSuccessStatusCode();
        var asset = await (await collaborator.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("contributed.png", "image/png", 100))).Content
            .ReadFromJsonAsync<AssetResponse>();
        (await collaborator.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/assets",
            new BrandAssetLinkRequest(asset!.Id, "product", "Contributed product")))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK,
            (await owner.GetAsync($"/api/v1/blob/assets/{asset.Id}/read-sas")).StatusCode);
        var thumbsResponse = await owner.PostAsJsonAsync("/api/v1/blob/assets/thumbs",
            new { assetIds = new[] { asset.Id } });
        thumbsResponse.EnsureSuccessStatusCode();
        var thumbs = await thumbsResponse.Content.ReadFromJsonAsync<List<AssetThumb>>();
        Assert.Equal(asset.Id, Assert.Single(thumbs!).AssetId);
    }

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);

    private sealed class BlockingMintBlobStore : IBlobSasService
    {
        public bool IsConfigured => true;
        public TaskCompletionSource MintStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowMint { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Uri> MintAsync(
            string blobPath, BlobSasPermissions permission, int? minutes, CancellationToken ct)
        {
            MintStarted.TrySetResult();
            await AllowMint.Task.WaitAsync(ct);
            return new Uri($"https://storage.test/private/{Uri.EscapeDataString(blobPath)}?sp=r");
        }

        public Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<(Stream Stream, long Length)?> OpenReadAsync(string blobPath, CancellationToken ct) =>
            Task.FromResult<(Stream, long)?>(null);
        public Task WriteAsync(string blobPath, Stream content, string contentType, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<bool> ExistsAsync(string blobPath, CancellationToken ct) => Task.FromResult(false);
        public Task StageBlockAsync(string blobPath, string blockId, Stream content, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CommitBlocksAsync(
            string blobPath, IReadOnlyList<string> blockIds, string contentType, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(string blobPath, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ImmediateBlobStore : IBlobSasService
    {
        public bool IsConfigured => true;
        public Task<Uri> MintAsync(
            string blobPath, BlobSasPermissions permission, int? minutes, CancellationToken ct) =>
            Task.FromResult(new Uri($"https://storage.test/private/{Uri.EscapeDataString(blobPath)}?sp=r"));
        public Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<(Stream Stream, long Length)?> OpenReadAsync(string blobPath, CancellationToken ct) =>
            Task.FromResult<(Stream, long)?>(null);
        public Task WriteAsync(string blobPath, Stream content, string contentType, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<bool> ExistsAsync(string blobPath, CancellationToken ct) => Task.FromResult(true);
        public Task StageBlockAsync(string blobPath, string blockId, Stream content, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CommitBlocksAsync(
            string blobPath, IReadOnlyList<string> blockIds, string contentType, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(string blobPath, CancellationToken ct) => Task.CompletedTask;
    }
}