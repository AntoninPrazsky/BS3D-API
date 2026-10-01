using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;

namespace BS3D.Api.AdminWeb;

/// <summary>HTML that is safe to send as it is: only <see cref="Html.M"/> and <see cref="Html.Join"/> make one.</summary>
public readonly struct Markup
{
    private readonly string? _value;

    internal Markup(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public override string ToString() => Value;
}

/// <summary>
/// The admin page's only way to write HTML (issue #5, requirement 10). In <c>Html.M($"&lt;td&gt;{name}&lt;/td&gt;")</c> the
/// literal text is the page's own markup and every hole is encoded, unless it is already <see cref="Markup"/>: a
/// nickname, a user agent or a refusal's detail is attacker text and can only ever arrive as text. There is no way to
/// pass a string in as markup, so forgetting to encode is not an available mistake.
/// </summary>
public static class Html
{
    public static Markup M(ref MarkupHandler markup) => markup.ToMarkup();

    public static Markup Join(IEnumerable<Markup> parts) => new(string.Concat(parts.Select(p => p.Value)));

    public static string Encode(string? text) => HtmlEncoder.Default.Encode(text ?? string.Empty);
}

[InterpolatedStringHandler]
public ref struct MarkupHandler
{
    private readonly StringBuilder _builder;

    public MarkupHandler(int literalLength, int formattedCount) => _builder = new StringBuilder(literalLength + formattedCount * 16);

    public void AppendLiteral(string literal) => _builder.Append(literal);

    public void AppendFormatted(Markup markup) => _builder.Append(markup.Value);

    public void AppendFormatted(string? text) => _builder.Append(Html.Encode(text));

    public void AppendFormatted<T>(T value) => _builder.Append(Html.Encode(Convert.ToString(value, CultureInfo.InvariantCulture)));

    public void AppendFormatted<T>(T value, string format) where T : IFormattable =>
        _builder.Append(Html.Encode(value.ToString(format, CultureInfo.InvariantCulture)));

    internal Markup ToMarkup() => new(_builder.ToString());
}
