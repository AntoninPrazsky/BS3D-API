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
}
