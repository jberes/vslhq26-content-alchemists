namespace Castmill.UI.Tests;

/// <summary>
/// Enforces G1: every page, component and layout lives in Castmill.UI, so the two shells
/// cannot drift apart (ADR-F01). This runs in the same `dotnet test` as everything else
/// and needs no Docker, which is the point — the rule is checked on every local run, not
/// only in CI.
/// </summary>
public sealed class UiBoundaryTests
{
    [Fact]
    public void No_razor_files_live_outside_the_shared_ui_library()
    {
        var root = RepositoryRoot();
        var sharedUi = Path.Combine(root, "src", "Castmill.UI");

        var strays = new[] { "src", "tests" }
            .Select(dir => Path.Combine(root, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.razor", SearchOption.AllDirectories))
            .Where(path => !path.StartsWith(sharedUi, StringComparison.Ordinal))
            .Where(path => !IsBuildOutput(path, root))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            $"UI must live only in src/Castmill.UI (G1). Move these there:{Environment.NewLine}"
            + string.Join(Environment.NewLine, strays));
    }

    [Fact]
    public void The_shells_contain_no_component_scoped_css_of_their_own()
    {
        var root = RepositoryRoot();

        // A shell may style its pre-startup boot chrome (wwwroot/css/host.css) and nothing
        // else; component-scoped CSS in a shell would be UI escaping the RCL by the back door.
        var strays = new[] { "Castmill.Web", "Castmill.Desktop" }
            .Select(shell => Path.Combine(root, "src", shell))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.razor.css", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path, root))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            $"Component-scoped CSS belongs in src/Castmill.UI (G1). Found:{Environment.NewLine}"
            + string.Join(Environment.NewLine, strays));
    }

    private static bool IsBuildOutput(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "obj" or "bin" or "node_modules");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Castmill.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
