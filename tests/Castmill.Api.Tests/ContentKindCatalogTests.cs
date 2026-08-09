using Castmill.Api.Services.Ai;
using Castmill.Core;

namespace Castmill.Api.Tests;

public sealed class ContentKindCatalogTests
{
    [Fact]
    public void Every_user_content_kind_resolves_to_a_real_generator()
    {
        Assert.All(ArtifactKinds.UserContent, kind =>
            Assert.True(kind == "blog" || Generators.Find(kind) is not null,
                $"{kind} is user-facing but has no generator."));
    }

    [Fact]
    public void Every_content_generator_is_in_the_shared_user_inventory()
    {
        var contentGenerators = Generators.FanOut
            .Where(spec => spec.Kind is not ("image-prompts" or "thumbnail-concepts" or "seo-brief"))
            .Select(spec => spec.Kind)
            .Append("blog")
            .Order();

        Assert.Equal(ArtifactKinds.UserContent.Order(), contentGenerators);
    }
}
