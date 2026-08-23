using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Evidence;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class EvidenceFoundationTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Transcript_ingest_creates_ordered_media_evidence_and_retry_does_not_duplicate()
    {
        var (client, campaignId) = await SignedInCampaignAsync("evidence-ingest");

        var first = await IngestAsync(client, campaignId);
        var retry = await IngestAsync(client, campaignId);
        Assert.Equal(first, retry);

        var sources = await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources");
        var source = Assert.Single(sources!);
        Assert.Equal(first, source.LegacyArtifactId);
        Assert.Equal(SourceKinds.Transcript, source.Kind);
        Assert.Equal(SourceModalities.Media, source.Modality);
        Assert.NotNull(source.ApprovedEvidence);
        Assert.Equal(1, source.ApprovedEvidence!.Revision);
        Assert.Equal(64, source.ApprovedEvidence.Hash.Length);

        var evidence = await client.GetFromJsonAsync<EvidenceRevisionResponse>(
            $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence?approved=true");
        Assert.Equal(1, evidence!.Revision);
        Assert.True(evidence.IsApproved);
        Assert.Equal(new[] { "s01", "s02" }, evidence.Blocks.Select(block => block.StableId));

        var firstBlock = evidence.Blocks[0];
        Assert.Equal(EvidenceLocatorKinds.MediaTimeRange, firstBlock.LocatorKind);
        Assert.Equal(0, firstBlock.Ordinal);
        Assert.Equal(0.0, firstBlock.Locator.GetProperty("startSeconds").GetDouble());
        Assert.Equal(4.25, firstBlock.Locator.GetProperty("endSeconds").GetDouble());
        Assert.Equal("part-one.mp4", firstBlock.Locator.GetProperty("sourceLabel").GetString());

        var secondBlock = evidence.Blocks[1];
        Assert.Equal(4.25, secondBlock.Locator.GetProperty("startSeconds").GetDouble());
        Assert.Equal(9.0, secondBlock.Locator.GetProperty("endSeconds").GetDouble());
        Assert.Equal("part-two.wav", secondBlock.Locator.GetProperty("sourceLabel").GetString());
    }

    [Fact]
    public async Task Approved_projection_omits_excluded_blocks_after_revision_approval()
    {
        var (client, campaignId) = await SignedInCampaignAsync("evidence-approval");
        await IngestAsync(client, campaignId);
        var source = Assert.Single((await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources"))!);
        var originalHash = source.ApprovedEvidence!.Hash;

        var revise = await client.PatchAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence/s02",
            new EvidenceBlockRevisionRequest(null, true));
        revise.EnsureSuccessStatusCode();
        var draft = (await revise.Content.ReadFromJsonAsync<EvidenceRevisionResponse>())!;
        Assert.Equal(2, draft.Revision);
        Assert.False(draft.IsApproved);
        Assert.True(Assert.Single(draft.Blocks, block => block.StableId == "s02").IsExcluded);

        var stillApproved = await client.GetFromJsonAsync<EvidenceRevisionResponse>(
            $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence?approved=true");
        Assert.Equal(1, stillApproved!.Revision);
        Assert.Equal(2, stillApproved.Blocks.Count);

        var approve = await client.PostAsync(
            $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence/2/approve",
            null);
        approve.EnsureSuccessStatusCode();

        var approved = await client.GetFromJsonAsync<EvidenceRevisionResponse>(
            $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence?approved=true");
        Assert.Equal(2, approved!.Revision);
        Assert.True(approved.IsApproved);
        Assert.Equal("s01", Assert.Single(approved.Blocks).StableId);
        Assert.NotEqual(originalHash, approved.Source.ApprovedEvidence!.Hash);

        var excludedCitation = await client.GetFromJsonAsync<CitationResolutionResponse>(
            $"/api/v1/campaigns/{campaignId}/sources/citations/s02?sourceAssetId={source.Id}");
        Assert.False(excludedCitation!.Resolved);
    }

    [Fact]
    public async Task Concurrent_duplicate_approval_preserves_one_immutable_tuple()
    {
        var (client, campaignId) = await SignedInCampaignAsync("evidence-concurrent-approval");
        await IngestAsync(client, campaignId);
        var source = Assert.Single((await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources"))!);
        var revise = await client.PatchAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence/s02",
            new EvidenceBlockRevisionRequest("Corrected second proof.", null));
        revise.EnsureSuccessStatusCode();
        var draft = (await revise.Content.ReadFromJsonAsync<EvidenceRevisionResponse>())!;

        var approvals = await Task.WhenAll(
            client.PostAsync(
                $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence/{draft.Revision}/approve",
                null),
            client.PostAsync(
                $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence/{draft.Revision}/approve",
                null));
        Assert.All(approvals, response => response.EnsureSuccessStatusCode());
        var results = await Task.WhenAll(approvals.Select(response =>
            response.Content.ReadFromJsonAsync<EvidenceRevisionResponse>()));
        var tuples = results.Select(result => result!.Source.ApprovedEvidence!).ToList();
        Assert.Single(tuples.Select(tuple => tuple.RevisionId).Distinct());
        Assert.Single(tuples.Select(tuple => tuple.Hash).Distinct());
        Assert.Single(tuples.Select(tuple => tuple.ApprovedAt).Distinct());
    }

    [Fact]
    public async Task Source_and_evidence_http_routes_are_invisible_across_tenants()
    {
        var (aliceClient, campaignId) = await SignedInCampaignAsync("evidence-alice");
        await IngestAsync(aliceClient, campaignId);
        var source = Assert.Single((await aliceClient.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources"))!);
        var (bobClient, _) = await SignedInCampaignAsync("evidence-bob");

        Assert.Equal(HttpStatusCode.NotFound,
            (await bobClient.GetAsync($"/api/v1/campaigns/{campaignId}/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bobClient.GetAsync(
                $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence?approved=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bobClient.GetAsync(
                $"/api/v1/campaigns/{campaignId}/sources/citations/s01")).StatusCode);
    }

    [Fact]
    public async Task Legacy_string_citations_stay_in_previews_and_resolve_to_approved_evidence()
    {
        var (client, campaignId) = await SignedInCampaignAsync("evidence-citation");
        await IngestAsync(client, campaignId);

        var create = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest(
                "blog",
                "Legacy citation",
                JsonSerializer.Serialize(new { markdown = "Grounded copy.", citations = new[] { "s01" } })));
        create.EnsureSuccessStatusCode();
        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        Assert.Equal(new[] { "s01" }, Assert.Single(previews!, item => item.Kind == "blog").Citations);

        var resolution = await client.GetFromJsonAsync<CitationResolutionResponse>(
            $"/api/v1/campaigns/{campaignId}/sources/citations/s01");
        Assert.True(resolution!.Resolved);
        Assert.Equal("s01", resolution.Reference.EvidenceBlockId);
        Assert.Equal("local-whisper", resolution.SourceLabel);
        Assert.Equal("s01", resolution.Evidence!.StableId);
        Assert.NotNull(resolution.ApprovedEvidence);

        var source = Assert.Single((await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources"))!);
        var qualifiedId = CitationReferenceCodec.Format(source.Id, "s01");
        var qualified = await client.GetFromJsonAsync<CitationResolutionResponse>(
            $"/api/v1/campaigns/{campaignId}/sources/citations/{Uri.EscapeDataString(qualifiedId)}");
        Assert.True(qualified!.Resolved);
        Assert.Equal(source.Id, qualified.Reference.SourceAssetId);
        Assert.Equal("s01", qualified.Reference.EvidenceBlockId);
    }

    [Fact]
    public async Task Generation_load_backfills_a_legacy_transcript_once()
    {
        var (client, campaignId) = await SignedInCampaignAsync("evidence-backfill");
        var transcript = new TranscriptContent("legacy-media",
        [
            new TranscriptSegment("legacy-1", 0, 4, "Host", "Legacy proof remains grounded."),
        ]);
        var create = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest(
                "transcript",
                "Legacy transcript",
                JsonSerializer.Serialize(transcript, TranscriptService.Json)));
        create.EnsureSuccessStatusCode();
        var artifact = (await create.Content.ReadFromJsonAsync<ArtifactResponse>())!;

        var generations = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsJsonAsync(
                $"/api/v1/ai/campaigns/{campaignId}/generate/social-x",
                new { transcriptArtifactId = artifact.Id })));
        foreach (var generation in generations)
        {
            generation.EnsureSuccessStatusCode();
        }

        var sources = await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources");
        var source = Assert.Single(sources!);
        Assert.Equal(artifact.Id, source.LegacyArtifactId);
        Assert.NotNull(source.ApprovedEvidence);

        var evidence = await client.GetFromJsonAsync<EvidenceRevisionResponse>(
            $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence?approved=true");
        Assert.Equal("legacy-1", Assert.Single(evidence!.Blocks).StableId);
    }

    private static async Task<Guid> IngestAsync(HttpClient client, Guid campaignId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaignId}/transcripts",
            new TranscriptIngestRequest(
                "First recording contains proof. Second recording adds context.",
                "local-whisper",
                [
                    new TranscriptSegment(
                        "desktop-2", 4.25, 9, "Guest", "Second recording adds context.", "part-two.wav"),
                    new TranscriptSegment(
                        "desktop-1", 0, 4.25, "Host", "First recording contains proof.", "part-one.mp4"),
                ]));
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("transcriptArtifactId").GetGuid();
    }

    private async Task<(HttpClient Client, Guid CampaignId)> SignedInCampaignAsync(string prefix)
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(
                $"{prefix}-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple",
                "Evidence Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = await client.PostAsJsonAsync(
            "/api/v1/campaigns",
            new CampaignCreateRequest($"Evidence {Guid.NewGuid():N}", null));
        campaign.EnsureSuccessStatusCode();
        return (client, (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id);
    }
}

public sealed class EvidenceRevisionHasherTests
{
    [Fact]
    public void Excluded_blocks_are_structurally_omitted_from_the_approved_hash()
    {
        var included = Block("s01", 0, "Included proof", false);
        var excluded = Block("s02", 1, "Discarded aside", true);
        var first = EvidenceRevisionHasher.HashApproved([included, excluded]);

        excluded.Content = "A completely different discarded aside";
        excluded.ContentHash = EvidenceRevisionHasher.HashContent(excluded.Content);
        var second = EvidenceRevisionHasher.HashApproved([included, excluded]);
        Assert.Equal(first, second);

        included.Content = "Changed included proof";
        included.ContentHash = EvidenceRevisionHasher.HashContent(included.Content);
        Assert.NotEqual(first, EvidenceRevisionHasher.HashApproved([included, excluded]));
    }

    private static EvidenceBlock Block(string stableId, int ordinal, string content, bool excluded) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        CampaignId = Guid.NewGuid(),
        SourceAssetId = Guid.NewGuid(),
        StableId = stableId,
        Ordinal = ordinal,
        Content = content,
        ContentHash = EvidenceRevisionHasher.HashContent(content),
        LocatorKind = EvidenceLocatorKinds.MediaTimeRange,
        LocatorJson = """{"startSeconds":0,"endSeconds":1}""",
        Revision = 1,
        RevisionId = Guid.NewGuid(),
        ApprovalState = EvidenceApprovalStates.Approved,
        IsExcluded = excluded,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };
}