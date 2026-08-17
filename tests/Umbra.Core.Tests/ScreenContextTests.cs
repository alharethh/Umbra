using Umbra.Core.Wear;

namespace Umbra.Core.Tests;

/// <summary>
/// Design-spec §2.4 — the comparison that decides whether the wear thesis is worth anything, and
/// the one the README has to state honestly.
/// </summary>
public class ScreenContextTests
{
    private static WearBudget PinnedIcon => DutyBudget.Evaluate(
        TierDuty.Current,
        Rgb.FromHex("#333333"),
        Rgb.FromHex("#404040"),
        DutyBudget.Tier2CappedLuminance);

    [Fact]
    public void Bar_Is_191x_Below_LightMode_Desktop_Content()
    {
        Approx.Equal(0.684310, WearModel.Rate(ScreenContext.LightWindowBody), 1e-6, "light window body wear");
        Approx.Equal(191.0, ScreenContext.RatioTo(ScreenContext.LightWindowBody, PinnedIcon), 1.0,
            "ratio vs light desktop (L = 0.80)");
    }

    [Fact]
    public void Bar_Is_86x_Below_MidTone_Content()
    {
        Approx.Equal(0.307786, WearModel.Rate(ScreenContext.MidToneContent), 1e-6, "mid-tone wear");
        Approx.Equal(86.0, ScreenContext.RatioTo(ScreenContext.MidToneContent, PinnedIcon), 1.0,
            "ratio vs mid-tone content (L = 0.50)");
    }

    [Fact]
    public void Bar_Is_Roughly_Level_With_DarkMode_Text()
    {
        Approx.Equal(0.006141, WearModel.Rate(ScreenContext.DarkModeText), 1e-6, "dark-mode text wear");
        Approx.Equal(1.7, ScreenContext.RatioTo(ScreenContext.DarkModeText, PinnedIcon), 0.05,
            "ratio vs dark-mode text and UI");
    }

    [Fact]
    public void Bar_Is_576x_Above_A_DarkMode_Application_Surface()
    {
        // "Read the last row carefully, because it decides the motion layer's fate." On a
        // dark-mode desktop the icon row is plausibly the single most static-and-lit region on the
        // panel — and unlike application content it never moves, never scrolls, and is present in
        // every session. This is the user the wear thesis is for.
        Approx.Equal(0.000620, WearModel.Rate(ScreenContext.DarkModeSurface), 1e-6, "#1E1E1E surface wear");

        double ratio = ScreenContext.RatioTo(ScreenContext.DarkModeSurface, PinnedIcon);
        Assert.True(ratio < 1.0, "Against a dark-mode surface the bar is the brighter of the two.");
        Approx.Equal(5.76, 1.0 / ratio, 0.01, "how far the bar sits above a dark-mode surface");
    }

    [Fact]
    public void The_Readme_Claim_Is_Bounded_At_Both_Ends()
    {
        // What the README must say: 86–191× below light-mode desktop content, ~5.8× above a
        // dark-mode application surface. The wear thesis is real for dark-mode users and close to
        // irrelevant for light-mode ones. Overclaiming here is the fastest way to make the project
        // look unserious.
        double light = ScreenContext.RatioTo(ScreenContext.LightWindowBody, PinnedIcon);
        double mid = ScreenContext.RatioTo(ScreenContext.MidToneContent, PinnedIcon);
        double dark = 1.0 / ScreenContext.RatioTo(ScreenContext.DarkModeSurface, PinnedIcon);

        Assert.InRange(mid, 86.0, light);
        Assert.InRange(light, 190.0, 192.0);
        Assert.InRange(dark, 5.5, 6.0);
    }
}
