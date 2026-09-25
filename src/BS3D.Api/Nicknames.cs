using System.Text;

namespace BS3D.Api;

/// <summary>
/// What a nickname may be (issue #2): 3 to 16 characters after normalization — composed to NFC, trimmed, runs of
/// spaces closed to one — made of letters, digits, single spaces, <c>_</c> and <c>-</c>, and not one of the denied
/// names. The game holds a typed name to the same rule and to a stricter alphabet of its own (the letters its fonts
/// can draw, BS3D's <c>Game/Online/Nickname.cs</c>), so what the game sends this accepts.
/// </summary>
public static class Nicknames
{
    public const int MinLength = 3;
    public const int MaxLength = 16;

    /// <summary>The name as it is kept and shown, or null when it is not one.</summary>
    public static string? Normalize(string? raw, IEnumerable<string> denied)
    {
        if (raw == null) return null;

        StringBuilder built = new(raw.Length);
        bool space = false;

        foreach (Rune rune in raw.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                space = built.Length > 0;
                continue;
            }

            bool allowed = Rune.IsLetterOrDigit(rune) || rune.Value == '_' || rune.Value == '-';
            if (!allowed) return null;

            if (space) built.Append(' ');
            space = false;
            built.Append(rune.ToString());
        }

        string name = built.ToString();
        int length = name.EnumerateRunes().Count();

        if (length < MinLength || length > MaxLength) return null;
        if (denied.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase))) return null;

        return name;
    }
}
