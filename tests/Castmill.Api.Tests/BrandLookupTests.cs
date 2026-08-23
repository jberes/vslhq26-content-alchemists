using System.Net;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Evidence;

namespace Castmill.Api.Tests;

/// <summary>
/// The brand lookup makes the server fetch a URL the CALLER chose, which makes it a
/// server-side request forgery primitive unless it is guarded. The interesting target is not
/// the open internet — it is everything reachable from the API's own network: the cloud
/// metadata endpoint at 169.254.169.254, the database, internal admin surfaces.
///
/// These pin the guard and the extraction. They are pure unit tests: no container, no network.
/// </summary>
public sealed class BrandLookupTests
{
    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:0000::1")]
    [InlineData("2002:7f00:1::")]
    public void Non_global_ipv6_addresses_are_private_source_targets(string address)
    {
        Assert.True(PublicUrlGuard.IsPrivate(System.Net.IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]  // cloud metadata — the classic target
    [InlineData("http://127.0.0.1:5005/api/v1/campaigns")]
    [InlineData("http://localhost/admin")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://172.16.4.9/")]
    public async Task Private_and_link_local_hosts_are_refused(string url)
    {
        var lookup = new BrandLookup(new NeverCalledFactory(), new NeverCalledRegistry());

        var ex = await Assert.ThrowsAsync<BrandLookupException>(
            () => lookup.LookupAsync(Guid.NewGuid(), url, notes: null, CancellationToken.None));

        // Refused before any socket is opened — the factory throws if it is ever used.
        Assert.Contains("private address", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/")]
    [InlineData("gopher://example.com/")]
    public async Task Only_http_and_https_are_allowed(string url)
    {
        var lookup = new BrandLookup(new NeverCalledFactory(), new NeverCalledRegistry());

        var ex = await Assert.ThrowsAsync<BrandLookupException>(
            () => lookup.LookupAsync(Guid.NewGuid(), url, notes: null, CancellationToken.None));

        Assert.Contains("http", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_malformed_url_is_a_message_not_a_crash()
    {
        var lookup = new BrandLookup(new NeverCalledFactory(), new NeverCalledRegistry());

        await Assert.ThrowsAsync<BrandLookupException>(
            () => lookup.LookupAsync(Guid.NewGuid(), "not a url at all", notes: null, CancellationToken.None));
    }

    [Fact]
    public async Task Neither_a_url_nor_notes_is_refused_before_anything_is_called()
    {
        var lookup = new BrandLookup(new NeverCalledFactory(), new NeverCalledRegistry());

        var ex = await Assert.ThrowsAsync<BrandLookupException>(
            () => lookup.LookupAsync(Guid.NewGuid(), url: null, notes: "   ", CancellationToken.None));

        Assert.Contains("URL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A private URL must still be refused when pasted context is supplied alongside it —
    /// otherwise "add some notes" would be a trivial bypass of the SSRF guard.
    /// </summary>
    [Fact]
    public async Task Notes_do_not_excuse_a_private_url()
    {
        var lookup = new BrandLookup(new NeverCalledFactory(), new NeverCalledRegistry());

        await Assert.ThrowsAsync<BrandLookupException>(
            () => lookup.LookupAsync(
                Guid.NewGuid(), "http://169.254.169.254/", "our voice is direct", CancellationToken.None));
    }

    [Fact]
    public void Extraction_reads_the_signals_a_site_actually_declares()
    {
        const string html = """
            <html><head>
              <title>Acme — build faster</title>
              <meta name="description" content="Tools for .NET teams." />
              <meta property="og:site_name" content="Acme" />
              <meta name="theme-color" content="#0a66c2" />
              <style>
                body { color: #1a1815; font-family: "IBM Plex Sans", sans-serif; }
                .a { background: #1a1815; } .b { background: #1a1815; } .c { color: #ffffff; }
              </style>
            </head><body>
              <script>var noise = "should not appear";</script>
              <h1>Ship it</h1><p>Real copy here.</p>
            </body></html>
            """;

        var page = BrandLookup.Extract(html, new Uri("https://acme.example/"));

        Assert.Equal("Acme", page.SiteName);
        Assert.Equal("Tools for .NET teams.", page.Description);

        // theme-color is promoted to the front regardless of how often it appears.
        Assert.Equal("#0A66C2", page.Colors[0]);
        // Frequency ordering: #1A1815 appears three times, #FFFFFF once.
        Assert.Contains("#1A1815", page.Colors);
        var ordered = page.Colors.ToList();
        Assert.True(
            ordered.IndexOf("#1A1815") < ordered.IndexOf("#FFFFFF"),
            "A colour the site repeats is more likely to be its palette than a one-off.");

        Assert.Contains(page.Fonts, f => f.Contains("IBM Plex Sans", StringComparison.Ordinal));

        // Script bodies are not brand copy and must never reach the model as though they were.
        Assert.DoesNotContain("should not appear", page.Text, StringComparison.Ordinal);
        Assert.Contains("Ship it", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Three_digit_hex_is_expanded_rather_than_dropped()
    {
        var page = BrandLookup.Extract(
            "<html><head><style>a{color:#0af}</style></head><body>x</body></html>",
            new Uri("https://acme.example/"));

        Assert.Contains("#00AAFF", page.Colors);
    }

    /// <summary>Fails the test if the guard ever lets a request through.</summary>
    private sealed class NeverCalledFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("The SSRF guard must reject before any fetch.");
    }

    private sealed class NeverCalledRegistry : IChatProviderRegistry
    {
        public Task<Microsoft.Extensions.AI.IChatClient> ResolveAsync(
            Guid userId, string modelAlias, CancellationToken ct) =>
            throw new InvalidOperationException("The model must not be called for a refused URL.");

        public Task<string> ResolveNameAsync(Guid userId, string modelAlias, CancellationToken ct) =>
            Task.FromResult("none");

        public Task<IReadOnlyList<ChatProviderStatus>> StatusAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatProviderStatus>>([]);
    }
}
