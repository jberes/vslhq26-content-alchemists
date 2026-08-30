using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Core.Auth;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class CampaignSharingTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Owner_can_share_by_pending_email_or_domain_edit_and_revoke()
    {
        var domain = $"campaign-{Guid.NewGuid():N}.example";
        var owner = await RegisterAsync($"owner@{domain}", "Campaign Owner");
        var domainMember = await RegisterAsync($"member@{domain}", "Domain Member");
        var unrelated = await RegisterAsync(
            $"unrelated-{Guid.NewGuid():N}@other.example", "Unrelated User");
        var pendingEmail = $"pending-{Guid.NewGuid():N}@outside.example";

        var createBrand = await owner.PostAsJsonAsync("/api/v1/brands",
            new BrandProfileUpsertRequest("Campaign brand", new BrandStyleCard(Voice: "Direct")));
        createBrand.EnsureSuccessStatusCode();
        var brand = (await createBrand.Content.ReadFromJsonAsync<BrandProfileDetailResponse>())!;
        var createAsset = await owner.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("campaign-logo.png", "image/png", 100));
        createAsset.EnsureSuccessStatusCode();
        var asset = (await createAsset.Content.ReadFromJsonAsync<AssetResponse>())!;
        (await owner.PostAsJsonAsync($"/api/v1/brands/{brand.Id}/assets",
            new BrandAssetLinkRequest(asset.Id, "logo", "Campaign logo")))
            .EnsureSuccessStatusCode();

        var createCampaign = await owner.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Shared campaign", "Original brief", brand.Id));
        createCampaign.EnsureSuccessStatusCode();
        var campaign = (await createCampaign.Content.ReadFromJsonAsync<CampaignResponse>())!;
        Assert.True(campaign.IsOwner);

        var createArtifact = await owner.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("blog", "Collaborative draft", """{"markdown":"v1"}"""));
        createArtifact.EnsureSuccessStatusCode();
        var artifact = (await createArtifact.Content.ReadFromJsonAsync<ArtifactResponse>())!;

        Assert.Equal(HttpStatusCode.NotFound,
            (await domainMember.GetAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await unrelated.GetAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);

        var pendingGrant = await owner.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/collaborators",
            new CampaignCollaboratorRequest(pendingEmail));
        Assert.Equal(HttpStatusCode.Created, pendingGrant.StatusCode);
        var grant = (await pendingGrant.Content
            .ReadFromJsonAsync<CampaignCollaboratorResponse>())!;
        Assert.Null(grant.DisplayName);

        var collaborator = await RegisterAsync(pendingEmail, "Invited Collaborator");
        var sharedCampaign = await collaborator.GetFromJsonAsync<CampaignResponse>(
            $"/api/v1/campaigns/{campaign.Id}");
        Assert.False(sharedCampaign!.IsOwner);
        Assert.Contains((await collaborator.GetFromJsonAsync<List<CampaignResponse>>(
            "/api/v1/campaigns"))!, item => item.Id == campaign.Id && !item.IsOwner);
        (await collaborator.GetAsync($"/api/v1/blob/assets/{asset.Id}/read-sas"))
            .EnsureSuccessStatusCode();

        var artifactResponse = await collaborator.GetAsync(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}");
        artifactResponse.EnsureSuccessStatusCode();
        using var updateArtifact = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}")
        {
            Content = JsonContent.Create(new ArtifactUpdateRequest(
                "Edited together", """{"markdown":"v2"}""")),
        };
        updateArtifact.Headers.IfMatch.Add(new EntityTagHeaderValue(
            artifactResponse.Headers.ETag!.Tag));
        var artifactUpdate = await collaborator.SendAsync(updateArtifact);
        Assert.Equal(HttpStatusCode.OK, artifactUpdate.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/campaigns/{campaign.Id}/sharing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.DeleteAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);

        var enableDomain = await owner.PutAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/sharing",
            new CampaignSharingRequest(DomainEnabled: true));
        enableDomain.EnsureSuccessStatusCode();
        var sharing = (await enableDomain.Content.ReadFromJsonAsync<CampaignSharingResponse>())!;
        Assert.True(sharing.DomainEnabled);
        Assert.Equal(domain, sharing.Domain);
        Assert.Equal(grant.Id, Assert.Single(sharing.Collaborators).Id);

        var domainCampaign = await domainMember.GetFromJsonAsync<CampaignResponse>(
            $"/api/v1/campaigns/{campaign.Id}");
        Assert.False(domainCampaign!.IsOwner);
        (await domainMember.GetAsync($"/api/v1/blob/assets/{asset.Id}/read-sas"))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,
            (await unrelated.GetAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);

        var revoke = await owner.DeleteAsync(
            $"/api/v1/campaigns/{campaign.Id}/collaborators/{grant.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await collaborator.GetAsync($"/api/v1/blob/assets/{asset.Id}/read-sas")).StatusCode);

        var disableDomain = await owner.PutAsJsonAsync(
            $"/api/v1/campaigns/{campaign.Id}/sharing",
            new CampaignSharingRequest(DomainEnabled: false));
        disableDomain.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,
            (await domainMember.GetAsync($"/api/v1/campaigns/{campaign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await domainMember.GetAsync($"/api/v1/blob/assets/{asset.Id}/read-sas")).StatusCode);

        var ownerArtifact = await owner.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}");
        Assert.Equal("Edited together", ownerArtifact!.Title);
    }

    private async Task<HttpClient> RegisterAsync(string email, string displayName)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "correct-horse-battery-staple", displayName));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }
}