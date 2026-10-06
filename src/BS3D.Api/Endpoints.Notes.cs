using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BS3D.Api;

/// <summary>
/// <c>POST /v1/notes</c> (#10, BS3D#813, additive to contract v1): a player's note about what they were looking at.
/// Anyone may send one, with a picture, onto a disk at the owner's home, so it is held tighter than a score: per-address
/// limits by the minute and by the day (an IPv6 address by its /64), a global count per UTC day, a cap on the notes
/// stored, a request size of its own, a text with no control characters, a context that is a small JSON object whose
/// every string is text, a picture shaped like a JPEG within its size, and a cap on the pictures kept, past which a note
/// is stored without its picture. A note's text and its address are never logged.
/// </summary>
public static partial class Endpoints
{
    private static void MapNoteEndpoint(WebApplication app)
    {
        // The one request bigger than the 4 KB Kestrel holds every other to (Program.cs): the routing middleware applies
        // the endpoint's own limit before the body is read
        long limit = app.Services.GetRequiredService<IOptions<ScoresOptions>>().Value.NoteMaxRequestBytes;
        app.MapPost("/v1/notes", PostNote).WithMetadata(new RequestSizeLimitAttribute(limit));
    }

    /// <summary>Whether every property name and string in <paramref name="element"/> decodes as text.</summary>
    private static bool IsText(JsonElement element)
    {
        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    _ = element.GetString();
                    return true;
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                        if (property.Name is null || !IsText(property.Value)) return false;
                    return true;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                        if (!IsText(item)) return false;
                    return true;
                default:
                    return true;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IResult PostNote(NoteRequest? body, HttpContext http, ScoreStore store, IOptions<ScoresOptions> options,
        RateLimits limits, AddressHasher addresses, TimeProvider clock, ILogger<ScoreStore> log)
    {
        ScoresOptions o = options.Value;

        if (body == null || body.NoteId == Guid.Empty) return Refuse(log, http, 400, Reasons.BadRequest, "missing note id");

        string network = AddressHasher.NetworkOf(http);
        if (!limits.TryTake("note-minute:" + network, o.NotesPerMinutePerAddress, out TimeSpan wait)
            || !limits.TryTake("note-day:" + network, o.NotesPerDayPerAddress, TimeSpan.FromDays(1), out wait))
            return RateLimited(log, http, wait, "address " + addresses.Hash(http));

        using SqliteConnection c = store.Open();

        // A retry out of the game's outbox: answered as the first time, stored once
        if (store.FindNoteAnswer(c, body.NoteId) is { } known) return Results.Text(known, "application/json", statusCode: 200);

        // Linked only to a player who exists and whose token this is. A player the service has never seen is not made
        // here (a score makes one), so their note keeps its name as a claim, and their id and token's hash, so that
        // removing themselves once they exist takes this note too (BS3D#548's "everything they sent")
        Guid? linked = null, claimedId = null;
        string? claimedToken = null;
        if (body.PlayerId is Guid playerId && playerId != Guid.Empty)
        {
            string? token = Tokens.FromRequest(http.Request);
            if (token == null) return Refuse(log, http, 401, Reasons.NoToken, playerId.ToString());
            if (store.FindPlayer(c, playerId) is { } player)
            {
                if (!Tokens.Matches(token, player.TokenHash)) return Refuse(log, http, 401, Reasons.WrongToken, playerId.ToString());
                linked = playerId;
            }
            else
            {
                claimedId = playerId;
                claimedToken = Tokens.Hash(token);
            }
        }

        string? claimed = null;
        if (body.Name != null)
        {
            claimed = Nicknames.Normalize(body.Name, o.DeniedNames);
            if (claimed == null) return Refuse(log, http, 422, Reasons.BadName, "note");
        }

        // The detail of a refused note never holds its text: a refusal is logged, and a note is the player's words
        string? text = NoteText.Normalize(body.Text, o.NoteMaxLength, out string? textProblem);
        if (text == null) return Refuse(log, http, 422, textProblem!, $"{body.Text?.Length ?? 0} characters");

        if (body.GameVersion == null || !GameVersionPattern().IsMatch(body.GameVersion))
            return Refuse(log, http, 422, Reasons.BadVersion, body.GameVersion ?? "(none)");

        string context = "{}";
        if (body.Context is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return Refuse(log, http, 422, Reasons.BadContext, element.ValueKind.ToString());
            // Every name and every string decoded once: the raw text is stored as it came, and a lone surrogate's escape
            // in it would be stored fine and then break every reader of the note (the admin page and the CLI)
            if (!IsText(element)) return Refuse(log, http, 422, Reasons.BadContext, "a string that is not text");
            context = element.GetRawText();
            int bytes = Encoding.UTF8.GetByteCount(context);
            if (bytes > o.NoteMaxContextBytes) return Refuse(log, http, 422, Reasons.ContextTooLarge, $"{bytes} bytes");
        }

        byte[]? picture = null;
        int width = 0, height = 0;
        if (body.Screenshot != null)
        {
            // Measured before it is decoded: base64 is 4 characters for every 3 bytes
            if (body.Screenshot.Length > (o.NoteMaxPictureBytes + 2) / 3 * 4)
                return Refuse(log, http, 422, Reasons.PictureTooLarge, $"{body.Screenshot.Length} base64 characters");

            byte[] buffer = new byte[body.Screenshot.Length / 4 * 3];
            if (!Convert.TryFromBase64String(body.Screenshot, buffer, out int written))
                return Refuse(log, http, 422, Reasons.BadPicture, "not base64");
            if (written > o.NoteMaxPictureBytes) return Refuse(log, http, 422, Reasons.PictureTooLarge, $"{written} bytes");
            if (!Jpeg.TryMeasure(buffer.AsSpan(0, written), out width, out height))
                return Refuse(log, http, 422, Reasons.BadPicture, $"not a JPEG ({written} bytes)");
            if (width > o.NoteMaxPictureSide || height > o.NoteMaxPictureSide)
                return Refuse(log, http, 422, Reasons.PictureTooLarge, $"{width}x{height}");
            picture = buffer[..written];
        }

        DateTimeOffset now = clock.GetUtcNow();

        ScoreStore.Begin(c);
        try
        {
            // The day's count is read inside the write lock, so two notes cannot both be the last one
            DateTimeOffset day = new(now.UtcDateTime.Date, TimeSpan.Zero);
            if (store.NotesSince(c, day) >= o.NotesPerDay)
            {
                ScoreStore.Rollback(c);
                return RateLimited(log, http, day.AddDays(1) - now, "every address (the day's notes)");
            }

            // The notes stored at their cap: refused, and kept in the game's outbox until the owner makes room
            if (store.NotesStored(c) >= o.NotesMaxStored)
            {
                ScoreStore.Rollback(c);
                http.Response.Headers.RetryAfter = "3600";
                return Refuse(log, http, 429, Reasons.NotesFull, $"{o.NotesMaxStored} notes stored");
            }

            if (picture != null && store.StoredPictureBytes(c) + picture.Length > o.NotesMaxStoredPictureBytes)
            {
                log.LogWarning("A note's picture was not kept: the pictures stored have reached {Cap} bytes (delete old notes)",
                    o.NotesMaxStoredPictureBytes);
                picture = null;
            }

            NoteAnswer answer = new(body.NoteId, ScreenshotStored: picture != null);
            string answerJson = JsonSerializer.Serialize(answer, Json);

            store.InsertNote(c, new ScoreStore.NewNote(body.NoteId, now, linked, claimedId, claimedToken, claimed, text, body.GameVersion,
                context, addresses.Hash(http), picture, width, height, answerJson));
            ScoreStore.Commit(c);

            return Results.Json(answer, Json, statusCode: 201);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            // The same note id arrived twice at once and the other one won the insert: answer as a retry
            ScoreStore.Rollback(c);
            return store.FindNoteAnswer(c, body.NoteId) is { } raced
                ? Results.Text(raced, "application/json", statusCode: 200)
                : Refuse(log, http, 400, Reasons.BadRequest, e.Message);
        }
        catch
        {
            ScoreStore.Rollback(c);
            throw;
        }
    }
}
