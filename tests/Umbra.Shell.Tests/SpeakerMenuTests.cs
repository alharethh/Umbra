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

    // Backlog 3p: the foreground borrow on the press left the bar's own window in front, and keys
    // typed after the menu closed went nowhere. Our menu has no owner to grant anything to.
    [Fact]
    public void A_Right_Press_That_Opens_Our_Menu_Does_Not_Borrow_The_Foreground() =>
        Assert.False(SpeakerMenu.GrantsOwnerForeground(rightButton: true, Volume, 0));

    [Fact]
    public void A_Left_Press_On_The_Speaker_Still_Borrows() =>
        // ManagedShell opens Quick Settings for it; not ours to change.
        Assert.True(SpeakerMenu.GrantsOwnerForeground(rightButton: false, Volume, 0));

    [Fact]
    public void Every_Other_Right_Press_Still_Borrows()
    {
        // An owner putting up its own menu needs the grant, or the menu does not dismiss.
        Assert.True(SpeakerMenu.GrantsOwnerForeground(rightButton: true, Volume, 0x4C9));
        Assert.True(SpeakerMenu.GrantsOwnerForeground(rightButton: true, Guid.Empty, 0));
        Assert.True(SpeakerMenu.GrantsOwnerForeground(
            rightButton: true, Guid.Parse(NotificationArea.POWER_GUID), 0x4C9));
    }
}
