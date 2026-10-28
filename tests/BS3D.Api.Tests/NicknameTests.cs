namespace BS3D.Api.Tests;

public sealed class NicknameTests
{
    private static readonly string[] Denied = ["admin"];

    [Theory]
    [InlineData("Tester", "Tester")]
    [InlineData("  Pražský  2 ", "Pražský 2")]
    [InlineData("a_b-c", "a_b-c")]
    [InlineData("Жаворонок", "Жаворонок")]       //the service takes any letter; the game is the stricter of the two
    [InlineData("1234567890123456", "1234567890123456")]
    public void A_nickname_is_normalized(string raw, string expected) =>
        Assert.Equal(expected, Nicknames.Normalize(raw, Denied));

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("12345678901234567")]
    [InlineData("no!")]
    [InlineData("emoji 😀")]
    [InlineData("ADMIN")]
    [InlineData("tab\tbed")]                          //a tab is whitespace, closed to one space — kept; see below
    public void A_non_nickname_is_refused(string? raw)
    {
        string? name = Nicknames.Normalize(raw, Denied);
        if (raw == "tab\tbed") Assert.Equal("tab bed", name);
        else Assert.Null(name);
    }

    [Fact]
    public void A_letter_typed_as_base_and_combining_mark_is_one_letter() =>
        Assert.Equal("Café", Nicknames.Normalize("Café", Denied));

    /// <summary>BS3D#1020: a denied word anywhere in the name, however it is cased, accented, split or spelled with digits.</summary>
    [Theory]
    [InlineData("KuloMrd")]
    [InlineData("zmrd")]
    [InlineData("MRDKA")]
    [InlineData("k_u_r_v_a")]
    [InlineData("kurv4")]
    [InlineData("Kokot 7")]
    [InlineData("Čůrák")]
    [InlineData("Píčus")]
    [InlineData("FuckYou")]
    [InlineData("sh1thead")]
    [InlineData("Hitler88")]
    [InlineData("BigAss")]
    [InlineData("pica123")]
    [InlineData("Píča")]
    [InlineData("kunda")]
    [InlineData("Sex Machine")]
    [InlineData("Kulo Mrd")]
    [InlineData("Ne Gr")]
    [InlineData("Mr D")]
    [InlineData("f u c k")]
    [InlineData("Kurwa")]
    public void A_name_holding_a_denied_word_is_refused(string raw) => Assert.Null(Nicknames.Normalize(raw, Denied));

    /// <summary>And not the ordinary names that hold a short one inside them, which are only refused as a whole part.</summary>
    [Theory]
    [InlineData("Picard")]
    [InlineData("Bass")]
    [InlineData("Kundera")]
    [InlineData("Fickle")]
    [InlineData("Analog")]
    [InlineData("Sexton")]
    [InlineData("Hancock")]
    [InlineData("Peacock")]
    [InlineData("Dickens")]
    [InlineData("Swanky")]
    [InlineData("Antonín Pražský")]
    [InlineData("Titan")]
    [InlineData("Cucumber")]
    public void An_ordinary_name_that_holds_a_short_word_is_kept(string raw) => Assert.Equal(raw, Nicknames.Normalize(raw, Denied));

    /// <summary>
    /// Nor two ordinary words whose join holds a denied word (BS3D#1036): the words are looked in one at a time, and only runs
    /// of one- and two-letter words are joined, so the gap between two real words is never read across.
    /// </summary>
    [Theory]
    [InlineData("Jane Grey")]
    [InlineData("Anne Green")]
    [InlineData("Mr Darcy")]
    [InlineData("Mr_Dobbs")]
    [InlineData("Hans Lutz")]
    [InlineData("Chris Hitchens")]
    [InlineData("Wash It")]
    [InlineData("Pen Isaac")]
    [InlineData("Ben Iggy")]
    [InlineData("Java Gina")]
    public void Two_ordinary_words_are_not_read_across_their_gap(string raw) => Assert.Equal(raw, Nicknames.Normalize(raw, Denied));
}
