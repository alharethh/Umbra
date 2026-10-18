using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using ManagedShell.WindowsTray;

namespace Umbra.Shell.Shell;

/// <summary>
/// What a screen reader hears for a notification icon.
/// </summary>
/// <remarks>
/// <para>
/// The icon's own tooltip is the right answer whenever there is one, and usually there is. When
/// there is not, the container's automation name comes out empty — and an empty name is not
/// silence: WPF's item peer falls back to <c>ToString()</c> on the bound object, which for
/// ManagedShell's <see cref="NotifyIcon"/> is its type name. Measured with UI Automation on the
/// 150% laptop: a packaged app in the overflow flyout and the power indicator in the strip were both
/// announced as <c>ManagedShell.WindowsTray.NotifyIcon</c>. The same trap <c>TaskbarSlot</c> fell
/// into, arriving through a type that is not ours to override.
/// </para>
/// <para>
/// So "no name" is not one of the options; the choice is only what to say instead. In order: the
/// tooltip; Windows' own name for the indicators that are identified by GUID, because they are
/// all hosted by <c>explorer.exe</c> and its file description would call the battery "Windows
/// Explorer"; the owning executable's file description, which is the name the application gives
/// itself in Task Manager; its file name; and only then a generic phrase.
/// </para>
/// </remarks>
internal static class TrayIconName
{
    /// <summary>Said when nothing better is known. Generic, but not a type name.</summary>
    public const string Fallback = "Notification icon";

    /// <param name="title">The icon's tooltip, possibly empty.</param>
    /// <param name="guid">The icon's GUID, possibly empty.</param>
    /// <param name="path">The owning process's executable, possibly empty.</param>
    /// <param name="describe">Reads an executable's file description; null when it has none.</param>
    public static string For(string? title, Guid guid, string? path, Func<string, string?> describe)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title;

        string? indicator = SystemIndicator(guid);
        if (indicator is not null) return indicator;

        if (string.IsNullOrWhiteSpace(path)) return Fallback;

        // Explorer hosts Windows' own icons, so its description names the host, not the icon.
        string file = Path.GetFileName(path);
        if (string.Equals(file, "explorer.exe", StringComparison.OrdinalIgnoreCase)) return Fallback;

        string? description = describe(path);
        if (!string.IsNullOrWhiteSpace(description)) return description.Trim();

        string bare = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(bare) ? Fallback : bare;
    }

    /// <summary>Windows' own name for an indicator it identifies by GUID, or null.</summary>
    private static string? SystemIndicator(Guid guid)
    {
        if (guid == Guid.Empty) return null;

        string text = guid.ToString();
        if (Matches(NotificationArea.POWER_GUID)) return "Battery";
        if (Matches(NotificationArea.VOLUME_GUID)) return "Volume";
        if (Matches(NotificationArea.NETWORK_GUID)) return "Network";
        return null;

        bool Matches(string known) => string.Equals(text, known, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly ConcurrentDictionary<string, string?> Descriptions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The executable's file description, read once per path. A version-resource read on a local
    /// file, not a shell call, so it cannot wedge the way icon extraction did; cached anyway,
    /// because the binding re-evaluates whenever the tooltip changes.
    /// </summary>
    public static string? Describe(string path) =>
        Descriptions.GetOrAdd(path, static p =>
        {
            try
            {
                return File.Exists(p) ? FileVersionInfo.GetVersionInfo(p).FileDescription : null;
            }
            catch (Exception)
            {
                return null;
            }
        });
}

/// <summary>Binds a notification icon's title, GUID and path to <see cref="TrayIconName"/>.</summary>
internal sealed class TrayIconNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        string? title = values.Length > 0 ? values[0] as string : null;
        Guid guid = values.Length > 1 && values[1] is Guid g ? g : Guid.Empty;
        string? path = values.Length > 2 ? values[2] as string : null;

        return TrayIconName.For(title, guid, path, TrayIconName.Describe);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
