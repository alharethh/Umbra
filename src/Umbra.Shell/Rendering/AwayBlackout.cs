namespace Umbra.Shell.Rendering;

/// <summary>
/// Decides when the bar goes fully black because nobody is there: no input for
/// <see cref="DelaySeconds"/>, and nothing playing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Watching a video is not being away</b> (the project owner's condition for building this).
/// Input alone cannot tell the two apart: a video player keeps the display on through a power
/// request, and <c>GetLastInputInfo</c> is not reset by power requests. Another process's power
/// requests are not readable without elevation, so the signal used is the one any video with
/// sound produces: an active audio session on a playback device (<see cref="Shell.AudioPlayback"/>).
/// It is asked only once the input delay has run out, and then at most every
/// <see cref="PlaybackRecheckSeconds"/>, so a playlist that moves on to the next video wakes a
/// blacked-out bar within that time.
/// </para>
/// <para>
/// Pure, with the idle time, the clock and the playback question injected, so the rule can be
/// tested without a window, a mouse or a sound card. The bar feeds it from the proximity tick it
/// already runs; nothing here adds a timer.
/// </para>
/// </remarks>
internal sealed class AwayBlackout(Func<bool> playbackActive)
{
    /// <summary>How often playback is asked again while the bar is past its delay.</summary>
    public const double PlaybackRecheckSeconds = 5.0;

    private double? _lastPlaybackCheck;
    private bool _playing;

    /// <summary>Seconds without input before the bar goes black. Zero or less turns it off.</summary>
    public double DelaySeconds { get; set; }

    /// <summary>Whether the bar should be black right now.</summary>
    public bool IsAway { get; private set; }

    /// <summary>Whether the last playback check found something playing.</summary>
    public bool HeldByPlayback => _playing && _lastPlaybackCheck is not null;

    /// <summary>
    /// Re-evaluates. Returns true when <see cref="IsAway"/> changed.
    /// </summary>
    /// <param name="idleSeconds">Seconds since the last keyboard or mouse input, system-wide.</param>
    /// <param name="nowSeconds">A monotonic clock, in seconds.</param>
    public bool Update(double idleSeconds, double nowSeconds)
    {
        bool away;

        if (DelaySeconds <= 0 || idleSeconds < DelaySeconds)
        {
            // Back at once, and the next time the delay runs out playback is asked straight away
            // rather than trusting an answer from before the user came back.
            away = false;
            _lastPlaybackCheck = null;
            _playing = false;
        }
        else
        {
            if (_lastPlaybackCheck is not { } last || nowSeconds - last >= PlaybackRecheckSeconds)
            {
                _playing = playbackActive();
                _lastPlaybackCheck = nowSeconds;
            }

            away = !_playing;
        }

        if (away == IsAway) return false;
        IsAway = away;
        return true;
    }

    /// <summary>
    /// The opacity that leaves <paramref name="lightFraction"/> of the content's light, drawn over
    /// black.
    /// </summary>
    /// <remarks>
    /// Opacity is applied to sRGB values, so a linear opacity ramp is not a linear ramp of light:
    /// half opacity is about a fifth of the light, and the fade would look like a drop followed by a
    /// long dim tail. Fading in linear light means the opacity follows the inverse transfer curve.
    /// The 2.2 power stands in for the piecewise sRGB curve, which is exact enough for a one-off
    /// transition and, unlike the piecewise curve, independent of the colour being faded.
    /// </remarks>
    public static double OpacityForLight(double lightFraction) =>
        Math.Pow(Math.Clamp(lightFraction, 0.0, 1.0), 1.0 / 2.2);
}
