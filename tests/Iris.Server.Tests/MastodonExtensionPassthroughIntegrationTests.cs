using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 13.1 — Mastodon extension passthrough. Mastodon-specific extension properties (the
/// <c>sensitive</c> boolean, the <c>toot:emoji</c> custom-emoji array, and the <c>poll</c> /
/// <c>Question</c> shape) are <em>not</em> in the ActivityStreams 2.0 vocabulary that
/// <c>KristofferStrube.ActivityStreams</c> models. Iris does not re-implement them (Rule 6): unknown
/// properties land in the library's <c>[JsonExtensionData]</c> and are forwarded opaquely. This test
/// proves the guarantee end-to-end — an inbound object carrying these Mastodon extensions is stored and
/// served back with every extension property preserved on the wire (the "don't drop unknown properties"
/// guarantee, F-01 / change 064, applied specifically to the Mastodon surface that Phase 13 live-interop
/// will exercise).
/// </summary>
public sealed class MastodonExtensionPassthroughIntegrationTests : IDisposable
{
    private const string Host = "mastodon.domain.local";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");
    private static readonly Iri NoteIri = new($"{ActorIri}/notes/n1");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{Host}";

    public MastodonExtensionPassthroughIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        Seed(_persistence);
        _server = StartServer(_persistence);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- A Note carrying the `sensitive` flag round-trips --------------------------------

    [Fact]
    public async Task Note_WithSensitiveFlag_RoundTripsThroughStore()
    {
        // Mastodon marks NSFW content with a top-level `sensitive` boolean. The library has no
        // `Sensitive` property (it is a Mastodon extension), so it lands in ExtensionData.
        var note = BuildNoteWithExtensions(sensitive: true);
        await _persistence.Objects.PutObjectAsync(note);

        var response = await _http.GetAsync(ObjectPath(NoteIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The standard AS2.0 properties are present...
        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(NoteIri.Value, doc.RootElement.GetProperty("id").GetString());
        // ...and the Mastodon `sensitive` extension survives the round-trip as a JSON boolean.
        Assert.True(doc.RootElement.GetProperty("sensitive").GetBoolean());
    }

    // --- A Note carrying `toot:emoji` custom emoji round-trips ----------------------------

    [Fact]
    public async Task Note_WithTootEmoji_RoundTripsThroughStore()
    {
        // Mastodon custom emoji appear as a `toot:emoji` array of {name, imageUrl, shortCode} objects.
        // This is a Mastodon extension (not in AS2.0), so it lands in ExtensionData.
        var note = BuildNoteWithExtensions(
            tootEmoji: JsonSerializer.SerializeToElement(new[]
            {
                new { name = "cat", imageUrl = "https://media.example.com/cat.png", shortCode = ":cat:" },
                new { name = "rocket", imageUrl = "https://media.example.com/rocket.png", shortCode = ":rocket:" },
            }));
        await _persistence.Objects.PutObjectAsync(note);

        var response = await _http.GetAsync(ObjectPath(NoteIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The `toot:emoji` extension is preserved verbatim (an array of two emoji objects).
        var emoji = doc.RootElement.GetProperty("toot:emoji");
        Assert.Equal(JsonValueKind.Array, emoji.ValueKind);
        Assert.Equal(2, emoji.GetArrayLength());
        Assert.Equal("cat", emoji[0].GetProperty("name").GetString());
        Assert.Equal(":rocket:", emoji[1].GetProperty("shortCode").GetString());
    }

    // --- Both extensions together round-trip ---------------------------------------------

    [Fact]
    public async Task Note_WithSensitiveAndTootEmoji_RoundTripsTogether()
    {
        var note = BuildNoteWithExtensions(
            sensitive: true,
            tootEmoji: JsonSerializer.SerializeToElement(new[]
            {
                new { name = "sparkle", imageUrl = "https://media.example.com/sparkle.png", shortCode = ":sparkle:" },
            }));
        await _persistence.Objects.PutObjectAsync(note);

        var response = await _http.GetAsync(ObjectPath(NoteIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(doc.RootElement.GetProperty("sensitive").GetBoolean());
        var emoji = doc.RootElement.GetProperty("toot:emoji");
        Assert.Equal(JsonValueKind.Array, emoji.ValueKind);
        Assert.Equal(1, emoji.GetArrayLength());
        Assert.Equal("sparkle", emoji[0].GetProperty("name").GetString());
    }

    // --- A `Question` (Mastodon poll) object round-trips as an opaque object ---------------

    [Fact]
    public async Task Question_PollObject_RoundTripsAsOpaqueObject()
    {
        // Mastodon polls are a `Question` object with `options`/`votes`/`endsAt`/`closed`/`oneOfMany`
        // properties. The library models `Question` as an IntransitiveActivity (not an Object), so a
        // poll *object* (as Mastodon embeds it in a Note) is not a recognized concrete type — it lands
        // in ExtensionData as an opaque object. This test proves the poll shape is preserved verbatim.
        var pollNote = new Note
        {
            Id = NoteIri.Value,
            Content = ["Who wins?"],
            AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
        };
        pollNote.ExtensionData ??= new Dictionary<string, JsonElement>();
        pollNote.ExtensionData["poll"] = JsonSerializer.SerializeToElement(new
        {
            id = "poll-1",
            options = new[]
            {
                new { title = "Alice", votesCount = 3 },
                new { title = "Bob", votesCount = 5 },
            },
            endsAt = "2026-09-01T00:00:00Z",
            expired = false,
            multiple = false,
            totalVotes = 8,
        });
        await _persistence.Objects.PutObjectAsync(pollNote);

        var response = await _http.GetAsync(ObjectPath(NoteIri));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The Note itself is intact...
        Assert.Equal("Note", doc.RootElement.GetProperty("type").GetString());
        // ...and the embedded `poll` object is preserved verbatim (an opaque Mastodon extension).
        var poll = doc.RootElement.GetProperty("poll");
        Assert.Equal("poll-1", poll.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Array, poll.GetProperty("options").ValueKind);
        Assert.Equal(2, poll.GetProperty("options").GetArrayLength());
        Assert.Equal(8, poll.GetProperty("totalVotes").GetInt32());
        Assert.False(poll.GetProperty("expired").GetBoolean());
    }

    // --- An actor document carrying Mastodon extensions round-trips -----------------------

    [Fact]
    public async Task Actor_WithMastodonExtensions_RoundTripsThroughActorDoc()
    {
        // A Mastodon actor document carries extensions like `manuallyApprovesFollowers` (boolean) and
        // `memorable` (boolean, a Mastodon-specific account flag). These are not in the AS2.0 vocabulary
        // the library models as typed properties, so they land in ExtensionData. The actor-document
        // endpoint serves them verbatim (the same wire shape the `blocks`/`flags`/`mutes` extensions use).
        var actor = new Person
        {
            Id = ActorIri.Value,
            PreferredUsername = Handle,
            Name = [Handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["manuallyApprovesFollowers"] = JsonSerializer.SerializeToElement(true);
        actor.ExtensionData["memorable"] = JsonSerializer.SerializeToElement(true);
        await _persistence.ActorStore.PutActorAsync(actor);

        var response = await _http.GetAsync($"/ap/v1/u/{Handle}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The standard actor properties are present...
        Assert.Equal("Person", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(Handle, doc.RootElement.GetProperty("preferredUsername").GetString());
        // ...and the Mastodon extensions survive the round-trip.
        Assert.True(doc.RootElement.GetProperty("manuallyApprovesFollowers").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("memorable").GetBoolean());
    }

    // --- Helpers ------------------------------------------------------------------------

    private static Note BuildNoteWithExtensions(bool? sensitive = null, JsonElement? tootEmoji = null)
    {
        var note = new Note
        {
            Id = NoteIri.Value,
            Content = ["a mastodon-flavored note"],
            AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
        };
        note.ExtensionData ??= new Dictionary<string, JsonElement>();
        if (sensitive is { } s)
        {
            note.ExtensionData["sensitive"] = JsonSerializer.SerializeToElement(s);
        }
        if (tootEmoji is { } emoji)
        {
            note.ExtensionData["toot:emoji"] = emoji;
        }
        return note;
    }

    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        var actor = new Person
        {
            Id = ActorIri.Value,
            PreferredUsername = Handle,
            Name = [Handle],
        };
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
        });

    private static string ObjectPath(Iri objectIri) => new Uri(objectIri.Value).AbsolutePath;
}
