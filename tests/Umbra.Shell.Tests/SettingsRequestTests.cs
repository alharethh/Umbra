using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// A second launch asks the running bar to open its settings.
/// </summary>
public class SettingsRequestTests
{
    private static string UniqueName() => $@"Local\Umbra.Test.OpenSettings.{Guid.NewGuid():N}";

    [Fact]
    public void A_Signal_Reaches_The_Listener_Every_Time()
    {
        string name = UniqueName();
        int requests = 0;
        using var arrived = new SemaphoreSlim(0);

        using (new SettingsRequest(() => { Interlocked.Increment(ref requests); arrived.Release(); }, name))
        {
            // Twice: the listener is not one-shot, so a third launch after closing the window
            // still opens it.
            Assert.True(SettingsRequest.Signal(name));
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "the first request never arrived");

            Assert.True(SettingsRequest.Signal(name));
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "the second request never arrived");
        }

        Assert.Equal(2, requests);
    }

    [Fact]
    public void With_No_Bar_Listening_The_Signal_Says_So_And_Creates_Nothing()
    {
        string name = UniqueName();

        Assert.False(SettingsRequest.Signal(name));

        // Signal opens; it must never create. A created event would sit there set, and the next
        // bar to start would open its settings window unasked.
        Assert.False(EventWaitHandle.TryOpenExisting(name, out _));
    }

    [Fact]
    public void A_Disposed_Listener_Hears_Nothing()
    {
        string name = UniqueName();
        int requests = 0;

        new SettingsRequest(() => Interlocked.Increment(ref requests), name).Dispose();

        Assert.False(SettingsRequest.Signal(name));
        Assert.Equal(0, requests);
    }

    [Fact]
    public void Only_An_Ordinary_Launch_Signals_Never_A_Restart()
    {
        Assert.True(SettingsRequest.ShouldSignal(restarting: false));
        Assert.False(SettingsRequest.ShouldSignal(restarting: true));
    }
}
