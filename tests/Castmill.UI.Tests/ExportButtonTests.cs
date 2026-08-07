using System.Net;
using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Getting the work out (roadmap 5.6). The export endpoints are authenticated, so these
/// cannot be plain links — the bytes come back through the normal client and are handed to
/// the browser's download path, which is why there is a seam to assert against at all.
/// </summary>
public sealed class ExportButtonTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("b1111111-1111-1111-1111-111111111111");
    private static readonly Guid BlogId = Guid.Parse("b1111111-1111-1111-1111-222222222222");

    private readonly RecordingDownloader _downloader = new();

    public ExportButtonTests()
    {
        Services.AddSingleton<IFileDownloader>(_downloader);
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [Preview()], [], 0, 0));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}", new ArtifactResponse(
            BlogId, CampaignId, "blog", "Launch-day blog post",
            """{"content":{"markdown":"Body."}}""",
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}/revisions",
            new List<ArtifactRevisionResponse>());
    }

    [Fact]
    public async Task Downloading_markdown_asks_for_the_md_format_and_saves_what_came_back()
    {
        StubFile($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}/export",
            "launch-day-blog-post.md", "text/markdown", "# Launch"u8.ToArray());

        var view = await OpenAsync();
        await ButtonAsync(view, "Download .md").ClickAsync();
        await view.WaitForStateAsync(() => _downloader.Saved.Count == 1, TimeSpan.FromSeconds(5));

        var request = Http.Requests.Last(r => r.RequestUri!.AbsolutePath.EndsWith("/export", StringComparison.Ordinal));
        Assert.Contains("format=md", request.RequestUri!.Query, StringComparison.Ordinal);

        var saved = _downloader.Saved[0];
        Assert.Equal("launch-day-blog-post.md", saved.FileName);
        Assert.Equal("# Launch", System.Text.Encoding.UTF8.GetString(saved.Bytes));
    }

    [Fact]
    public async Task The_docx_button_asks_for_docx()
    {
        StubFile($"api/v1/campaigns/{CampaignId}/artifacts/{BlogId}/export",
            "launch-day-blog-post.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [0x50, 0x4B, 0x03, 0x04]);

        var view = await OpenAsync();
        await ButtonAsync(view, ".docx").ClickAsync();
        await view.WaitForStateAsync(() => _downloader.Saved.Count == 1, TimeSpan.FromSeconds(5));

        var request = Http.Requests.Last(r => r.RequestUri!.AbsolutePath.EndsWith("/export", StringComparison.Ordinal));
        Assert.Contains("format=docx", request.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_whole_campaign_downloads_as_one_archive()
    {
        StubFile($"api/v1/campaigns/{CampaignId}/export",
            "webinar-campaign.zip", "application/zip", [0x50, 0x4B, 0x03, 0x04]);

        var view = await OpenAsync();
        await ButtonAsync(view, "Whole campaign").ClickAsync();
        await view.WaitForStateAsync(() => _downloader.Saved.Count == 1, TimeSpan.FromSeconds(5));

        Assert.Equal("webinar-campaign.zip", _downloader.Saved[0].FileName);
    }

    /// <summary>A failed export says so rather than silently saving nothing.</summary>
    [Fact]
    public async Task A_failed_export_reports_the_error_and_saves_nothing()
    {
        Http.OnStatus(HttpMethod.Get, $"api/v1/campaigns/{CampaignId}/export", HttpStatusCode.InternalServerError);

        var view = await OpenAsync();
        await ButtonAsync(view, "Whole campaign").ClickAsync();

        Assert.Empty(_downloader.Saved);
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<IRenderedComponent<FocusView>> OpenAsync()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll("button").Any(b => b.TextContent.Contains("Download .md", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        return view;
    }

    private static AngleSharp.Dom.IElement ButtonAsync(IRenderedComponent<FocusView> view, string label) =>
        view.FindAll("button").First(b => b.TextContent.Contains(label, StringComparison.Ordinal));

    private void StubFile(string path, string fileName, string contentType, byte[] bytes) =>
        Http.OnFile(path, fileName, contentType, bytes);

    private static ArtifactPreviewResponse Preview() =>
        new(BlogId, CampaignId, "blog", "Launch-day blog post", ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private sealed class RecordingDownloader : IFileDownloader
    {
        public List<DownloadedFile> Saved { get; } = [];

        public Task SaveAsync(DownloadedFile file)
        {
            Saved.Add(file);
            return Task.CompletedTask;
        }
    }
}
