namespace Castmill.UI.Tests;

public sealed class ShellScaleTests
{
    [Fact]
    public void Web_uses_browser_scale_while_desktop_keeps_its_retina_scale()
    {
        var root = FindRepositoryRoot();
        var baseCss = File.ReadAllText(Path.Combine(
            root, "src", "Castmill.UI", "wwwroot", "css", "base.css"));
        var webHost = File.ReadAllText(Path.Combine(
            root, "src", "Castmill.Web", "wwwroot", "index.html"));
        var desktopHost = File.ReadAllText(Path.Combine(
            root, "src", "Castmill.Desktop", "wwwroot", "index.html"));

        Assert.Contains("html.cm-shell-desktop", baseCss, StringComparison.Ordinal);
        Assert.Contains("font-size: 125%", baseCss, StringComparison.Ordinal);
        Assert.Contains("class=\"cm-shell-web\"", webHost, StringComparison.Ordinal);
        Assert.Contains("class=\"cm-shell-desktop\"", desktopHost, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Castmill.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}