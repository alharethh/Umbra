using System.Globalization;
using Umbra.Shell.Settings;

namespace Umbra.Shell.Rendering;

/// <summary>
/// What the clock's time line says, in 12 or 24 hours.
/// </summary>
/// <remarks>
/// <para>
/// <b>Follow Windows means the short time pattern.</b> <c>CultureInfo.CurrentCulture</c> carries
/// the user's own overrides from Windows' region settings, so a British locale with the clock set
/// to 12-hour reports <c>h:mm tt</c> here, not the locale's stock <c>HH:mm</c>. A lowercase
/// <c>h</c> outside quotes is a 12-hour pattern.
/// </para>
/// <para>
/// <b>The AM/PM designator is never empty.</b> Several cultures carry empty designators because
/// their clocks are 24-hour, and a forced 12-hour clock in one of them would read 2:05 in the
/// morning and the afternoon alike. The invariant culture's AM and PM stand in.
/// </para>
/// <para>
/// <b>A vertical bar drops the designator.</b> It is 56 DIP wide and <c>00:00:00</c> at 14 px
/// already takes 51.4 of them; <c>12:59:59 PM</c> is about 75. The date line under it is just as
/// full. So a 12-hour clock on a left or right bar reads <c>2:05</c>, as a phone's status bar
/// does, and the full time with AM or PM is in the clock's accessible name.
/// </para>
/// </remarks>
internal static class ClockText
{
    /// <summary>Whether <paramref name="format"/> means a 12-hour clock in <paramref name="culture"/>.</summary>
    public static bool IsTwelveHour(ClockFormat format, CultureInfo culture) => format switch
    {
        ClockFormat.TwelveHour => true,
        ClockFormat.TwentyFourHour => false,
        _ => PatternIsTwelveHour(culture.DateTimeFormat.ShortTimePattern),
    };

    /// <summary>Whether a .NET time pattern counts hours 1 to 12.</summary>
    /// <remarks>Text inside quotes is a literal and is skipped, so <c>'h'</c> is not an hour.</remarks>
    public static bool PatternIsTwelveHour(string pattern)
    {
        char? quote = null;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }

            switch (c)
            {
                case '\\': i++; break;
                case '\'' or '"': quote = c; break;
                case 'h': return true;
                case 'H': return false;
            }
        }

        return false;
    }

    /// <summary>The time line.</summary>
    public static string Time(DateTime now, bool twelveHour, bool seconds, bool vertical, CultureInfo culture)
    {
        if (!twelveHour) return now.ToString(seconds ? "HH:mm:ss" : "HH:mm", culture);

        string clock = now.ToString(seconds ? "h:mm:ss" : "h:mm", culture);
        return vertical ? clock : $"{clock} {Designator(now.Hour < 12, culture)}";
    }

    /// <summary>The full time for a screen reader, with AM or PM even where it is not drawn.</summary>
    public static string Spoken(DateTime now, bool twelveHour, CultureInfo culture) => twelveHour
        ? $"{now.ToString("h:mm", culture)} {Designator(now.Hour < 12, culture)}"
        : now.ToString("HH:mm", culture);

    /// <summary>
    /// The strings whose widest is the room a horizontal clock holds: every time it can show
    /// with seconds, digits at their widest.
    /// </summary>
    /// <remarks>
    /// Both designators, because AM and PM are not the same width and the clock must not grow at
    /// noon. Two hour digits even though <c>h</c> drops the leading one before ten, so the
    /// reserve covers 12:00:00.
    /// </remarks>
    public static IEnumerable<string> WidestCandidates(bool twelveHour, CultureInfo culture)
    {
        if (!twelveHour)
        {
            yield return "00:00:00";
            yield break;
        }

        yield return $"00:00:00 {Designator(true, culture)}";
        yield return $"00:00:00 {Designator(false, culture)}";
    }

    /// <summary>AM or PM in <paramref name="culture"/>, falling back to the invariant pair.</summary>
    public static string Designator(bool morning, CultureInfo culture)
    {
        string own = morning ? culture.DateTimeFormat.AMDesignator : culture.DateTimeFormat.PMDesignator;
        if (!string.IsNullOrWhiteSpace(own)) return own;

        return morning
            ? CultureInfo.InvariantCulture.DateTimeFormat.AMDesignator
            : CultureInfo.InvariantCulture.DateTimeFormat.PMDesignator;
    }
}
