using ManagedShell.WindowsTray;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// Right-clicking the speaker icon raised no menu: its owner registered no callback message, so the
/// forwarded click was <c>WM_NULL</c>. The bar now draws one of its own.
/// </summary>
public class SpeakerMenuTests
{
    private static readonly Guid Volume = Guid.Parse(NotificationArea.VOLUME_GUID);

    [Fact]
    public void The_Speaker_Icon_Gets_Our_Menu() =>
        // The volume icon as the bar received it on the 150% laptop: VOLUME_GUID, callback 0.
        Assert.True(SpeakerMenu.Replaces(Volume, 0));

    [Fact]
    public void A_Speaker_Icon_That_Can_Be_Forwarded_To_Is_Left_To_Windows() =>
        // A callback means the forwarded click arrives and Windows draws its own menu; ours as
        // well would be two menus.
        Assert.False(SpeakerMenu.Replaces(Volume, 0x4C9));

    [Fact]
    public void Other_Icons_With_No_Callback_Are_Not_Given_It()
    {
        Assert.False(SpeakerMenu.Replaces(Guid.Empty, 0));
        Assert.False(SpeakerMenu.Replaces(Guid.Parse(NotificationArea.POWER_GUID), 0));
        Assert.False(SpeakerMenu.Replaces(Guid.Parse(NotificationArea.NETWORK_GUID), 0));
    }

    [Fact]
    public void It_Offers_Windows_Entries_In_Windows_Order()
    {
        // Targets read out of SndVolSSO.dll on 25H2; the troubleshooter is Get Help now, not msdt.
        Assert.Equal(
            [
                "ms-contact-support://windows-speaker-icon//?Product=Windows&SearchKey=Audio%20Issues",
                "ms-settings:apps-volume",
                "ms-settings:sound",
            ],
            SpeakerMenu.Entries.Select(e => e.Target));

        Assert.All(SpeakerMenu.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Label)));
    }
}
