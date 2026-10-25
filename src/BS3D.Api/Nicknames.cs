using System.Globalization;
using System.Text;

namespace BS3D.Api;

/// <summary>
/// What a nickname may be (issue #2): 3 to 16 characters after normalization — composed to NFC, trimmed, runs of
/// spaces closed to one — made of letters, digits, single spaces, <c>_</c> and <c>-</c>, not one of the denied
/// names, and holding no denied word (BS3D#1020, <see cref="HasDeniedWord"/>). The game holds a typed name to the same
/// rule, its word lists in step with these, and to a stricter alphabet of its own (the letters its fonts can draw, BS3D's
/// <c>Game/Online/Nickname.cs</c>), so what the game sends this accepts.
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
        if (HasDeniedWord(name)) return null;

        return name;
    }

    /// <summary>
    /// Vulgar and hateful words no part of a name may hold (BS3D#1020: the owner, "KuloMrd" on the boards: "Mrd must be a
    /// forbidden word"), matched in the name <b>folded</b> (<see cref="Fold"/>): diacritics off, lower case, separators out,
    /// and the digits that stand for letters read as those letters, so "KuloMrd", "k_u_r_v_a" and "kurv4" are all caught.
    /// Czech and Slovak first, English and German after. A root that also sits inside ordinary names is not here but in
    /// <see cref="DeniedParts"/>. The game's copy is BS3D's <c>Nickname.DeniedWords</c>; change both in step.
    /// </summary>
    public static readonly string[] DeniedWords =
    [
        "mrd", "kurv", "kokot", "curak", "prdel", "hovn", "srac", "jebat", "jeban", "jebn", "zasran", "buzna", "buzerant",
        "negr", "kreten", "hajzl", "sukat", "picus", "picovin", "debil",
        "fuck", "shit", "cunt", "nigg", "fagg", "bitch", "whore", "slut", "porn", "nazi", "hitler", "penis", "vagin",
        "asshole", "bastard", "retard", "wanker", "dildo", "pussy", "motherf", "cocksuck", "blowjob",
        "ficken", "fotze", "arschloch",
    ];

    /// <summary>
    /// Words refused only as a whole part of a name, because they also sit inside ordinary ones ("pica" in Picard, "ass" in
    /// Bass, "kunda" in Kundera, "fick" in Fickle). A part is what spaces, <c>_</c>, <c>-</c>, digits and a change from a
    /// lower-case to a capital letter divide, folded as <see cref="DeniedWords"/> are but with no digit read as a letter:
    /// "BigAss" and "pica123" are refused, "Bass" and "Picard" are not. The game's copy is BS3D's <c>Nickname.DeniedParts</c>.
    /// </summary>
    public static readonly string[] DeniedParts =
    [
        "pica", "pice", "pici", "kunda", "suka", "jeb", "sex", "rape", "anal", "cock", "dick", "fag", "ass", "kys", "hure",
        "fick", "cum", "tit", "tits", "boob", "boobs",
    ];

    /// <summary>Whether <paramref name="name"/> holds a denied word anywhere, or a denied part as one of its parts.</summary>
    public static bool HasDeniedWord(string name)
    {
        string folded = Fold(name, digitsAsLetters: true);
        foreach (string word in DeniedWords)
            if (folded.Contains(word, StringComparison.Ordinal)) return true;

        int start = 0;
        for (int i = 1; i <= name.Length; i++)
        {
            bool end = i == name.Length || !char.IsLetter(name[i]) || (char.IsLower(name[i - 1]) && char.IsUpper(name[i]));
            if (!end) continue;
            string part = Fold(name[start..i], digitsAsLetters: false);
            if (part.Length > 0 && Array.IndexOf(DeniedParts, part) >= 0) return true;
            start = i;
            while (start < name.Length && !char.IsLetter(name[start])) start++;
            i = start;
        }
        return false;
    }

    /// <summary>
    /// A name as the word lists read it: decomposed and its combining marks dropped (so "Píča" reads "pica"), lower case,
    /// letters only; with <paramref name="digitsAsLetters"/> the digits that stand for letters read as them (0 o, 1 i, 3 e,
    /// 4 a, 5 s, 7 t, 8 b) and the rest dropped with the separators.
    /// </summary>
    public static string Fold(string name, bool digitsAsLetters)
    {
        StringBuilder folded = new(name.Length);
        foreach (char c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(c)) folded.Append(char.ToLowerInvariant(c));
            else if (digitsAsLetters && "0134578".IndexOf(c) is int at && at >= 0) folded.Append("oieastb"[at]);
        }
        return folded.ToString();
    }
}
