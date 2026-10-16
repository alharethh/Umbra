using System.Collections;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Text;
using Umbra.Shell;
using Umbra.Shell.Motion;
using Umbra.Shell.Wear;

namespace Umbra.Shell.Tests;

/// <summary>
/// The motion speed slider is in Debug builds and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// The slider breaks §6.1 while it runs and pauses wear recording, so it must not reach a user.
/// It is gated rather than deleted, and a gate is easy to leave half shut: a menu item written in
/// XAML survives every <c>#if</c>, because the preprocessor never sees the BAML. So these read the
/// built assembly itself, the compiled XAML included, rather than trusting the source.
/// </para>
/// <para>
/// Each test asserts both ways, so the same suite proves the instrument is still there under
/// <c>dotnet test</c> and gone under <c>dotnet test -c Release</c>. The test project's
/// configuration is the shell's, because the solution builds them together.
/// </para>
/// </remarks>
public class MotionSpeedGateTests
{
#if DEBUG
    private const bool Debug = true;
#else
    private const bool Debug = false;
#endif

    private static readonly Assembly Shell = typeof(BarWindow).Assembly;

    [Fact]
    public void The_Window_Type_Exists_Only_In_Debug()
    {
        Assert.Equal(Debug, Shell.GetType("Umbra.Shell.Motion.MotionSpeedWindow") is not null);
    }

    [Fact]
    public void The_Window_Xaml_Is_Compiled_Only_In_Debug()
    {
        Assert.Equal(Debug, BamlNames().Contains("motion/motionspeedwindow.baml"));
    }

    /// <summary>
    /// The one thing that could change the timer's interval, and the ceiling it is clamped to.
    /// Without them a release build ticks at §6.1's 2 Hz for its whole life.
    /// </summary>
    [Fact]
    public void Time_Scaling_Exists_Only_In_Debug()
    {
        Assert.Equal(Debug, typeof(MotionDriver).GetProperty("TimeScale") is not null);
        Assert.Equal(Debug, typeof(MotionDriver).GetField("MaxTimeScale") is not null);
    }

    /// <summary>
    /// The public way to stop wear being credited. The private flag stays, for session lock.
    /// </summary>
    [Fact]
    public void Wear_Can_Be_Suspended_From_Outside_Only_In_Debug()
    {
        Assert.Equal(Debug, typeof(WearSampler).GetProperty("Suspended") is not null);
        Assert.NotNull(typeof(WearSampler).GetField(
            "_suspended", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    /// <summary>
    /// The menu item is built in code, so the bar's BAML never names it in either build.
    /// </summary>
    [Fact]
    public void The_Bar_Xaml_Never_Declares_The_Menu_Item()
    {
        Assert.False(ContainsIgnoringCase(Baml("barwindow.baml"), "Motion speed", Encoding.UTF8));
        Assert.False(ContainsIgnoringCase(Baml("barwindow.baml"), "OnMotionSpeedClick", Encoding.UTF8));
    }

    /// <summary>
    /// The whole binary, in both encodings metadata uses: UTF-16 for string literals, UTF-8 for
    /// names and for BAML. A release Umbra.dll does not contain the words anywhere.
    /// </summary>
    [Fact]
    public void The_Words_Appear_In_The_Binary_Only_In_Debug()
    {
        byte[] image = File.ReadAllBytes(Shell.Location);

        bool found = ContainsIgnoringCase(image, "motion speed", Encoding.UTF8)
                     || ContainsIgnoringCase(image, "motion speed", Encoding.Unicode)
                     || ContainsIgnoringCase(image, "MotionSpeed", Encoding.UTF8)
                     || ContainsIgnoringCase(image, "MotionSpeed", Encoding.Unicode);

        Assert.Equal(Debug, found);
    }

    private static HashSet<string> BamlNames()
    {
        using ResourceReader reader = OpenResources();
        HashSet<string> names = [];
        foreach (DictionaryEntry entry in reader) names.Add((string)entry.Key);
        return names;
    }

    private static byte[] Baml(string name)
    {
        using ResourceReader reader = OpenResources();
        foreach (DictionaryEntry entry in reader)
        {
            if ((string)entry.Key != name) continue;
            using Stream stream = (Stream)entry.Value!;
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        throw new InvalidOperationException($"{name} is not in the shell's resources");
    }

    private static ResourceReader OpenResources() =>
        new(Shell.GetManifestResourceStream("Umbra.g.resources")
            ?? throw new InvalidOperationException("Umbra.g.resources not found"));

    /// <summary>ASCII case folding only, which is all these needles need.</summary>
    private static bool ContainsIgnoringCase(byte[] haystack, string needle, Encoding encoding)
    {
        byte[] lower = encoding.GetBytes(needle.ToLowerInvariant());
        byte[] upper = encoding.GetBytes(needle.ToUpperInvariant());

        for (int i = 0; i + lower.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < lower.Length && (haystack[i + j] == lower[j] || haystack[i + j] == upper[j])) j++;
            if (j == lower.Length) return true;
        }

        return false;
    }
}
