using System.Text;

namespace BS3D.Api;

/// <summary>
/// What a note's text may be (#10, BS3D#813): composed to NFC, its line breaks made <c>\n</c> and its tabs spaces,
/// trimmed, and then no other control character at all. A note is read on the admin page and printed by the admin CLI,
/// and a control character in either is either nothing or a terminal escape.
/// </summary>
public static class NoteText
{
    /// <summary>
    /// The text as it is stored, or null with the reason it is refused: <see cref="Reasons.EmptyNote"/>,
    /// <see cref="Reasons.NoteTooLong"/> or <see cref="Reasons.BadText"/> (a control character, or a string that is not
    /// text, which NFC refuses: a lone surrogate, though the JSON reader refuses that one's escape before this runs).
    /// </summary>
    public static string? Normalize(string? raw, int maxLength, out string? reason)
    {
        reason = null;
        string composed;
        try
        {
            composed = (raw ?? string.Empty).Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            reason = Reasons.BadText;
            return null;
        }

        StringBuilder built = new(composed.Length);
        for (int i = 0; i < composed.Length; i++)
        {
            char c = composed[i];
            if (c == '\r')
            {
                built.Append('\n');
                if (i + 1 < composed.Length && composed[i + 1] == '\n') i++;
            }
            else if (c == '\t') built.Append(' ');
            else if (c == '\n' || !char.IsControl(c)) built.Append(c);
            else
            {
                reason = Reasons.BadText;
                return null;
            }
        }

        string text = built.ToString().Trim();
        if (text.Length == 0) reason = Reasons.EmptyNote;
        else if (text.Length > maxLength) reason = Reasons.NoteTooLong;
        return reason == null ? text : null;
    }
}

/// <summary>
/// Whether a note's picture is a JPEG and how big (#10): its markers walked from SOI to the first scan, a frame header
/// (SOF) found on the way with its width and height, and EOI as its last two bytes. Nothing is decoded: the service does
/// not need the pixels, and a decoder is the part of an image library that has the bugs. What this refuses is anything
/// that is not shaped like a JPEG at all — a PNG, an archive, a script, a file cut short.
/// </summary>
public static class Jpeg
{
    /// <summary>True with the frame's size when <paramref name="bytes"/> is shaped like a JPEG; see the class doc.</summary>
    public static bool TryMeasure(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
        if (bytes[^2] != 0xFF || bytes[^1] != 0xD9) return false;

        int at = 2;
        while (at < bytes.Length)
        {
            if (bytes[at] != 0xFF) return false;
            // Fill bytes: any number of 0xFF before a marker
            while (at < bytes.Length && bytes[at] == 0xFF) at++;
            if (at >= bytes.Length) return false;

            byte marker = bytes[at++];
            // Markers with no length: TEM and the restart markers; an EOI before the first scan is a file with no picture
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            if (marker == 0xD9 || marker == 0x00) return false;

            if (at + 2 > bytes.Length) return false;
            int length = (bytes[at] << 8) | bytes[at + 1];
            if (length < 2 || at + length > bytes.Length) return false;
            ReadOnlySpan<byte> segment = bytes.Slice(at + 2, length - 2);

            // A frame header: every SOF but DHT (C4), JPG (C8) and DAC (CC), which share the range
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (segment.Length < 5) return false;
                height = (segment[1] << 8) | segment[2];
                width = (segment[3] << 8) | segment[4];
                if (width == 0 || height == 0) return false;
            }

            // The first scan: the entropy-coded data runs from here to the EOI checked above
            if (marker == 0xDA) return width > 0;

            at += length;
        }

        return false;
    }
}
