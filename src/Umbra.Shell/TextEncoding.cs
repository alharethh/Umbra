using System.Text;

namespace Umbra.Shell;

/// <summary>
/// The encoding for every text file the bar writes that can hold text from other processes.
/// </summary>
internal static class TextEncoding
{
    /// <summary>
    /// UTF-8, no BOM, writing U+FFFD for what it cannot encode instead of throwing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>File.AppendAllText</c> and <c>File.WriteAllLines</c> default to a UTF-8 encoder that
    /// throws <see cref="EncoderFallbackException"/> on a lone surrogate, and window titles carry
    /// them: ManagedShell cuts Windows Terminal's title in the middle of an emoji. Logging that
    /// title took the whole bar down from the dispatcher (2026-10-24), and a pin named after it
    /// would have done the same on save.
    /// </para>
    /// <para>
    /// Replacing keeps the line. For the log that is the evidence; for the pin file it is the pin.
    /// </para>
    /// </remarks>
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
}
