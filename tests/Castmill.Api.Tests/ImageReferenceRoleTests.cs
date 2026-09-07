using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Images;
using Castmill.Core;

namespace Castmill.Api.Tests;

/// <summary>
/// References are ordered and each is given a job (ADR-074). "3 reference images are attached"
/// told the model nothing about which was the backdrop and which the presenter — the "it
/// ignores my references" report. On an edits endpoint the first image is the one composed
/// onto, so the background must lead and the prompt must number them in send order.
/// </summary>
public sealed class ImageReferenceRoleTests
{
    [Fact]
    public void Background_leads_then_face_then_others_then_product()
    {
        Assert.True(ImageReferenceResolver.RoleRank("background") < ImageReferenceResolver.RoleRank("face"));
        Assert.True(ImageReferenceResolver.RoleRank("face") < ImageReferenceResolver.RoleRank("logo"));
        Assert.True(ImageReferenceResolver.RoleRank("logo") < ImageReferenceResolver.RoleRank("product"));
        Assert.Equal(ImageReferenceResolver.RoleRank("logo"), ImageReferenceResolver.RoleRank(null));
    }

    [Fact]
    public void Each_reference_is_numbered_with_its_job_in_send_order()
    {
        var slot = new ImageSlot
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), CampaignId = Guid.NewGuid(),
            Kind = "youtube-thumbnail", TargetWidth = 1280, TargetHeight = 720, State = "Empty",
            Prompt = "a bold thumbnail", PromptMode = "Manual",
        };
        var campaign = new Campaign { Id = slot.CampaignId, TenantId = slot.TenantId, OwnerId = Guid.NewGuid(), Name = "Launch" };
        IReadOnlyList<ImageReference> references =
        [
            new(Guid.NewGuid(), "office.jpg", "image/jpeg", [1], "background"),
            new(Guid.NewGuid(), "jason.jpg", "image/jpeg", [1], "face"),
            new(Guid.NewGuid(), "grid.png", "image/png", [1], "product"),
        ];

        var prompt = ImagePromptComposer.Compose(slot, campaign, null, BrandContext.Empty, null, references);

        var i1 = prompt.IndexOf("- Image 1: the BACKGROUND", StringComparison.Ordinal);
        var i2 = prompt.IndexOf("- Image 2: the PRESENTER", StringComparison.Ordinal);
        var i3 = prompt.IndexOf("- Image 3: the PRODUCT INTERFACE", StringComparison.Ordinal);
        Assert.True(i1 >= 0 && i2 > i1 && i3 > i2, prompt);
        Assert.Contains("3 reference images are attached, in this order", prompt, StringComparison.Ordinal);
        Assert.Contains("must be the person in the face reference", prompt, StringComparison.Ordinal);
        Assert.Contains("Never invent replacement UI", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_face_the_likeness_rule_is_not_added()
    {
        var slot = new ImageSlot
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), CampaignId = Guid.NewGuid(),
            Kind = "blog-hero", TargetWidth = 1200, TargetHeight = 675, State = "Empty",
            Prompt = "hero", PromptMode = "Manual",
        };
        var campaign = new Campaign { Id = slot.CampaignId, TenantId = slot.TenantId, OwnerId = Guid.NewGuid(), Name = "Launch" };

        var prompt = ImagePromptComposer.Compose(slot, campaign, null, BrandContext.Empty, null,
            [new ImageReference(Guid.NewGuid(), "grid.png", "image/png", [1], "product")]);

        Assert.Contains("- Image 1: the PRODUCT INTERFACE", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("face reference", prompt, StringComparison.Ordinal);
    }
}
