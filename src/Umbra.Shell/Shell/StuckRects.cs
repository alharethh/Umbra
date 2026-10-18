namespace Umbra.Shell.Shell;

/// <summary>
/// The one bit Umbra reads and writes in Explorer's saved taskbar settings: whether the user has
/// "Automatically hide the taskbar" switched on.
/// </summary>
/// <remarks>
/// <para>
/// Explorer keeps it in <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3</c>,
/// value <c>Settings</c>, a 48-byte binary blob whose format is not documented. What is known, and
/// what every guide to the setting relies on: it opens with its own size (<c>0x30</c> as a
/// little-endian 32-bit integer) and a version of <c>-2</c> (<c>FE FF FF FF</c>), and bit 0 of
/// byte 8 is auto-hide — <c>0x02</c> off, <c>0x03</c> on. Read on the second machine: exactly that
/// header, byte 8 <c>0x03</c>.
/// </para>
/// <para>
/// <b>Anything else is refused, not guessed at.</b> A blob of another length or with another header
/// is a format this code has never seen, and writing a bit into it could break the user's taskbar
/// in a way they cannot undo from Settings. Refusing costs only the repair, which is a backstop.
/// Only that bit is ever changed; every other byte goes back exactly as it was read.
/// </para>
/// </remarks>
internal static class StuckRects
{
    /// <summary>Where Explorer keeps the blob, under HKCU.</summary>
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";

    /// <summary>The value name.</summary>
    public const string ValueName = "Settings";

    private const int ExpectedLength = 48;
    private const int ExpectedVersion = -2;
    private const int FlagsOffset = 8;
    private const byte AutoHideBit = 0x01;

    /// <summary>True when the blob has the length and header this code knows.</summary>
    public static bool IsRecognised(byte[]? blob) =>
        blob is { Length: ExpectedLength } &&
        BitConverter.ToInt32(blob, 0) == ExpectedLength &&
        BitConverter.ToInt32(blob, 4) == ExpectedVersion;

    /// <summary>The saved auto-hide setting, or null when the blob is missing or unfamiliar.</summary>
    public static bool? ReadAutoHide(byte[]? blob) =>
        IsRecognised(blob) ? (blob![FlagsOffset] & AutoHideBit) != 0 : null;

    /// <summary>
    /// A copy of <paramref name="blob"/> with only the auto-hide bit changed, or null when the blob
    /// is missing or unfamiliar. The input is never modified.
    /// </summary>
    public static byte[]? WithAutoHide(byte[]? blob, bool autoHide)
    {
        if (!IsRecognised(blob)) return null;

        byte[] copy = (byte[])blob!.Clone();
        copy[FlagsOffset] = autoHide
            ? (byte)(copy[FlagsOffset] | AutoHideBit)
            : (byte)(copy[FlagsOffset] & ~AutoHideBit);

        return copy;
    }
}
