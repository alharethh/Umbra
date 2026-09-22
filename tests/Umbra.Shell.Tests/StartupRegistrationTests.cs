using Microsoft.Win32;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// Registering Umbra to start at sign-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>These touch the real registry, under the real Run key, and clean up after themselves.</b>
/// That is deliberate: the whole feature is one registry value, and a test against a fake would
/// assert that the code calls the methods the code calls. What can go wrong here — a value left
/// behind, a path stored unquoted, someone else's entry disturbed — only shows against the real
/// key.
/// </para>
/// <para>
/// They run under a value name of their own rather than the production one, so a test host that
/// dies mid-run cannot leave anything registered to start at the developer's next sign-in. The
/// value is saved and restored around each test regardless.
/// </para>
/// </remarks>
public class StartupRegistrationTests : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>
    /// <b>Not the real value name.</b> These write to the real Run key on purpose, and a test
    /// host that dies mid-run under the production name would leave <c>testhost.exe</c>
    /// registered to start at the developer's next sign-in. Under this one the worst case is a
    /// stray value nothing reads.
    /// </summary>
    private const string ValueName = "UmbraStartupRegistrationTests";

    private readonly string? _before;

    public StartupRegistrationTests()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        _before = key?.GetValue(ValueName) as string;
    }

    private static string? Stored()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>Enabling writes the entry; disabling removes it.</summary>
    [Fact]
    public void Enabling_Then_Disabling_Leaves_Nothing_Behind()
    {
        StartupRegistration.Apply(true, ValueName);

        Assert.True(StartupRegistration.IsEnabled(ValueName));
        Assert.NotNull(Stored());

        StartupRegistration.Apply(false, ValueName);

        Assert.False(StartupRegistration.IsEnabled(ValueName));
        Assert.Null(Stored());
    }

    /// <summary>
    /// The stored command names the running executable, in quotes.
    /// </summary>
    /// <remarks>
    /// A Run value is parsed as a command line, so an unquoted path through a folder with a space
    /// starts the wrong thing with arguments. Umbra usually lives somewhere without one, which is
    /// exactly why this would be found late.
    /// </remarks>
    [Fact]
    public void The_Stored_Command_Is_Quoted_And_Names_This_Executable()
    {
        StartupRegistration.Apply(true, ValueName);

        string? stored = Stored();

        Assert.NotNull(stored);
        Assert.StartsWith("\"", stored);
        Assert.EndsWith("\"", stored);
        Assert.Contains(Environment.ProcessPath!, stored, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applying the same state twice reports the second as no change.</summary>
    /// <remarks>
    /// The startup path calls this on every launch to repair a moved executable. If it wrote
    /// unconditionally it would be a registry write per launch, for nothing.
    /// </remarks>
    [Fact]
    public void Re_Applying_The_Same_State_Writes_Nothing()
    {
        StartupRegistration.Apply(true, ValueName);

        Assert.False(StartupRegistration.Apply(true, ValueName), "the second enable should be a no-op");

        StartupRegistration.Apply(false, ValueName);

        Assert.False(StartupRegistration.Apply(false, ValueName), "the second disable should be a no-op");
    }

    /// <summary>Disabling when nothing is registered is not an error.</summary>
    [Fact]
    public void Disabling_When_Absent_Is_Harmless()
    {
        StartupRegistration.Apply(false, ValueName);

        Assert.False(StartupRegistration.Apply(false, ValueName));
        Assert.False(StartupRegistration.IsEnabled(ValueName));
    }

    /// <summary>A stale path is corrected rather than left, and the change is reported.</summary>
    /// <remarks>
    /// The case that matters for the standalone download: run from Downloads, moved afterwards.
    /// Windows drops a Run entry whose target is gone without saying anything, so the symptom is
    /// "it stopped starting itself" with nothing to look at.
    /// </remarks>
    [Fact]
    public void A_Stale_Path_Is_Repointed()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
            key.SetValue(ValueName, "\"C:\\nowhere\\gone\\Umbra.exe\"", RegistryValueKind.String);

        Assert.True(StartupRegistration.Apply(true, ValueName), "a stale path should be corrected");
        Assert.Contains(Environment.ProcessPath!, Stored()!, StringComparison.OrdinalIgnoreCase);

        StartupRegistration.Apply(false, ValueName);
    }

    /// <summary>Quoting is idempotent and does not double-wrap.</summary>
    [Theory]
    [InlineData(@"C:\Apps\Umbra.exe", @"""C:\Apps\Umbra.exe""")]
    [InlineData(@"C:\Program Files\Umbra\Umbra.exe", @"""C:\Program Files\Umbra\Umbra.exe""")]
    [InlineData(@"""C:\Apps\Umbra.exe""", @"""C:\Apps\Umbra.exe""")]
    [InlineData(@"  C:\Apps\Umbra.exe  ", @"""C:\Apps\Umbra.exe""")]
    public void A_Path_Is_Quoted_Exactly_Once(string path, string expected) =>
        Assert.Equal(expected, StartupRegistration.Quote(path));

    public void Dispose()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

        if (_before is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, _before, RegistryValueKind.String);

        GC.SuppressFinalize(this);
    }
}
