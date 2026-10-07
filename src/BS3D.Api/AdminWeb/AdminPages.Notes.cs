using System.Globalization;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The Notes tab (#10, BS3D#813): what players wrote from the game's Send a Note, newest first, and a note's own page
/// with its text, its context and its picture. A note is attacker text like a nickname, so it is encoded and isolated in
/// <c>&lt;bdi&gt;</c>; its picture is served by this process only (<c>/note.jpg</c>), under the page's CSP.
/// </summary>
public static partial class AdminPages
{
    public static string Notes(IReadOnlyList<AdminData.NoteRow> notes, int total, TimeZoneInfo zone)
    {
        Markup rows = Html.Join(notes.Select(n => Html.M($"""
            <tr><td class="t"><a href="{NoteLink(n.Id)}">{Stamp(n.At, zone)}</a></td><td>{Who(n)}</td><td><bdi>{n.Level ?? ""}</bdi></td>
            <td><bdi>{n.Where ?? ""}</bdi></td><td><code>{n.GameVersion}</code></td><td class="n">{(n.PictureBytes > 0 ? Size(n.PictureBytes) : "")}</td>
            <td class="wrap"><a href="{NoteLink(n.Id)}"><bdi>{FirstLine(n.Text)}</bdi></a></td></tr>
            """)));
        Markup body = notes.Count == 0
            ? Html.M($"<p class=\"note\">No notes yet. A player sends one from the game's pause menu, the result page or the main menu (Send a Note).</p>")
            : Html.M($"""
                <p class="note">{(total > notes.Count ? Html.M($"The newest {notes.Count} of {total}") : Html.M($"{total}"))} notes players wrote in the game, newest first ({ZoneName(zone)}). Unlinked names are what the note claimed; a linked one is the player's nickname now. Read them on the box with <code>BS3D.Api admin notes --out &lt;folder&gt;</code>, delete one with <code>admin delete-note &lt;id&gt;</code>.</p>
                <div class="panel"><table><tr><th>Received</th><th>From</th><th>Level</th><th>Where</th><th>Game</th><th class="n">Picture</th><th>Note</th></tr>{rows}</table></div>
                """);
        return Layout("Notes", body, refresh: false);
    }

    public static string Note(AdminData.NoteView v, TimeZoneInfo zone)
    {
        AdminData.NoteRow n = v.Note;
        Markup picture = n.PictureBytes == 0 ? Html.M($"<p class=\"note\">No picture: the player unticked it, or the pictures stored had reached their cap.</p>")
            : Html.M($"""<p><img class="shot" src="/note.jpg?id={n.Id}" width="{v.PictureWidth}" height="{v.PictureHeight}" alt="The frame the player was looking at"></p><p class="note">{v.PictureWidth}×{v.PictureHeight}, {Size(n.PictureBytes)}</p>""");
        Markup context = v.Context.Count == 0 ? Html.M($"<p class=\"note\">The note carried no context.</p>")
            : Html.M($"""<div class="panel"><table class="facts">{Html.Join(v.Context.Select(f => Html.M($"<tr><th><bdi>{f.Key}</bdi></th><td><bdi>{f.Value}</bdi></td></tr>")))}</table></div>""");
        return Layout($"Note {n.Id}", Html.M($"""
            <p>{Who(n)} · {Stamp(n.At, zone)} {ZoneName(zone)} · <code>{n.GameVersion}</code> <code class="id">delete: BS3D.Api admin delete-note {n.Id}</code></p>
            <div class="panel"><p class="text"><bdi>{n.Text}</bdi></p></div>
            <h2>The picture</h2>{picture}
            <h2>The context</h2>{context}
            """), refresh: false, section: "Notes");
    }

    private static Markup Who(AdminData.NoteRow n) => n switch
    {
        { Linked: true, Player: Guid id } => Html.M($"<a href=\"{PlayerLink(id)}\"><bdi>{n.Name}</bdi></a>{Flags(n.Name!)}"),
        { Name: { } claimed } => Html.M($"<bdi>{claimed}</bdi>{Flags(claimed)} <span class=\"chip muted\">unverified</span>"),
        _ => Html.M($"<span class=\"chip muted\">anonymous</span>"),
    };

    /// <summary>The first line of a note, cut to fit a row.</summary>
    private static string FirstLine(string text)
    {
        string line = text.Split('\n', 2)[0];
        return line.Length <= 90 && line.Length == text.Length ? line : string.Concat(line.AsSpan(0, Math.Min(line.Length, 90)), "…");
    }

    private static string NoteLink(long id) => string.Create(CultureInfo.InvariantCulture, $"/note?id={id}");
}
