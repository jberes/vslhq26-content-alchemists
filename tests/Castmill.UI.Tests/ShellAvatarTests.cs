using System.Net;
using Bunit;
using Castmill.Core.Auth;
using Castmill.UI.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class ShellAvatarTests : CastmillUiTestContext
{
    [Fact]
    public void External_avatar_renders_as_an_in_memory_image()
    {
        Tokens.SignIn();
        Http.OnGet("api/v1/me", new MeResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "avatar@example.com",
            "Avatar User",
            HasAvatar: true));
        Http.OnFile(
            "api/v1/me/avatar",
            "avatar",
            "image/jpeg",
            [0xFF, 0xD8, 0xFF, 0xE0]);

        var shell = RenderShell();

        shell.WaitForAssertion(() =>
        {
            var image = Assert.Single(shell.FindAll("img.cm-user-avatar"));
            Assert.StartsWith("data:image/jpeg;base64,", image.GetAttribute("src"), StringComparison.Ordinal);
            Assert.Empty(shell.FindAll(".cm-user-avatar--fallback"));
        });
    }

    [Fact]
    public void Missing_avatar_renders_initials_without_requesting_image()
    {
        Tokens.SignIn();
        Http.OnGet("api/v1/me", new MeResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "local@example.com",
            "Local User"));

        var shell = RenderShell();

        shell.WaitForAssertion(() =>
        {
            Assert.Equal("LU", shell.Find(".cm-user-avatar--fallback").TextContent.Trim());
            Assert.DoesNotContain(
                Http.Requests,
                request => request.RequestUri?.PathAndQuery == "/api/v1/me/avatar");
        });
    }

    private IRenderedComponent<ShellLayout> RenderShell() => Render<ShellLayout>(parameters =>
        parameters.Add(
            component => component.Body,
            (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>Body</p>"))));
}
