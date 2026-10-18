using System.IO;
using System.Text.RegularExpressions;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Tests;

/// <summary>
/// The settings window's pointer to the front page's removal steps.
/// </summary>
/// <remarks>
/// A link to a heading that has been renamed does not fail: GitHub opens the top of the README and
/// the user is left to scroll for instructions they needed because something had already gone
/// wrong. So the anchor is checked against the README's actual headings, slugged the way GitHub
/// slugs them.
/// </remarks>
public class RemovalHelpTests
{
    [Fact]
    public void The_Link_Names_A_Heading_The_README_Has()
    {
        string anchor = SettingsWindow.RemovalHelpUrl.Split('#', 2)[1];

        Assert.Contains(anchor, ReadmeAnchors());
    }

    [Fact]
    public void The_Link_Points_At_The_Repository_Front_Page()
    {
        Assert.StartsWith("https://github.com/alharethh/Umbra#", SettingsWindow.RemovalHelpUrl);
    }

    [Theory]
    [InlineData("Getting your Windows taskbar back", "getting-your-windows-taskbar-back")]
    [InlineData("Where 260× comes from", "where-260-comes-from")]
    [InlineData("Umbra – OLED Optimized taskbar", "umbra--oled-optimized-taskbar")]
    public void Slugs_Follow_GitHub(string heading, string slug) =>
        Assert.Equal(slug, Slug(heading));

    private static IReadOnlyList<string> ReadmeAnchors() =>
        File.ReadLines(Path.Combine(RepoRoot(), "README.md"))
            .Where(line => line.StartsWith('#'))
            .Select(line => Slug(line.TrimStart('#').Trim()))
            .ToList();

    /// <summary>
    /// GitHub's rule: lower-case, drop everything that is not a letter, digit, space, hyphen or
    /// underscore, then turn each space into a hyphen. Spaces are not collapsed, which is why an
    /// en dash between two spaces leaves a double hyphen.
    /// </summary>
    private static string Slug(string heading) =>
        Regex.Replace(heading.ToLowerInvariant(), @"[^\p{L}\p{Nd}\s_-]", "").Replace(' ', '-');

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Umbra.slnx")))
                return dir.FullName;

        throw new InvalidOperationException("Umbra.slnx not found above " + AppContext.BaseDirectory);
    }
}
