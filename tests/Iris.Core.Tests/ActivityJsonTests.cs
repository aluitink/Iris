using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// Unit tests for the <see cref="ActivityJson"/> serialization entry point.
/// Per the coding style, serialization tests assert on the wire format (serialized JSON),
/// not just in-memory state.
/// </summary>
public class ActivityJsonTests
{
    [Fact]
    public void Serialize_Note_ContainsContextTypeAndId()
    {
        var note = new Note
        {
            Id = "https://a.domain.local/n/1",
            Content = ["<p>Hello</p>"],
        };

        var json = ActivityJson.Serialize(note);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("https://www.w3.org/ns/activitystreams", root.GetProperty("@context").GetString());
        Assert.Equal("Note", root.GetProperty("type").GetString());
        Assert.Equal("https://a.domain.local/n/1", root.GetProperty("id").GetString());
        Assert.Equal("<p>Hello</p>", root.GetProperty("content").GetString());
    }

    [Fact]
    public void Serialize_OmitsUnsetProperties()
    {
        var note = new Note
        {
            Id = "https://a.domain.local/n/2",
        };

        var json = ActivityJson.Serialize(note);

        // Name/Content/Summary were never set, so they must not appear on the wire.
        Assert.DoesNotContain("\"name\"", json);
        Assert.DoesNotContain("\"content\"", json);
        Assert.DoesNotContain("\"summary\"", json);
    }

    [Fact]
    public void Deserialize_Note_ResolvesConcreteType()
    {
        var json = """
            {
              "@context": "https://www.w3.org/ns/activitystreams",
              "type": "Note",
              "id": "https://a.domain.local/n/1",
              "content": "<p>hi</p>"
            }
            """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        Assert.IsType<Note>(payload);
        var note = (Note)payload;
        Assert.Equal("https://a.domain.local/n/1", note.Id);
        Assert.Equal("<p>hi</p>", note.Content!.First());
    }

    [Fact]
    public void Deserialize_Follow_WithNestedNote_ResolvesBoth()
    {
        var follow = new Follow
        {
            Id = "https://a.domain.local/f/1",
            Actor = [new Person { Id = "https://a.domain.local/u/alice" }],
            Object = [new Note { Id = "https://a.domain.local/n/9" }],
        };

        var json = ActivityJson.Serialize(follow);
        IObjectOrLink back = ActivityJson.Deserialize<IObjectOrLink>(json)!;

        Assert.IsType<Follow>(back);
        var f = (Follow)back;
        var actor = f.Actor!.First();
        var target = f.Object!.First();
        Assert.IsType<Person>(actor);
        Assert.IsType<Note>(target);
        Assert.Equal("https://a.domain.local/n/9", ((Note)target).Id);
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsMultiValuedAsArray()
    {
        var note = new Note
        {
            Id = "https://a.domain.local/n/3",
            To =
            [
                new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") },
                new Link { Href = new Uri("https://b.domain.local/u/bob") },
            ],
        };

        var json = ActivityJson.Serialize(note);

        // Two recipients serialize as a JSON array.
        using var doc = JsonDocument.Parse(json);
        var to = doc.RootElement.GetProperty("to");
        Assert.Equal(JsonValueKind.Array, to.ValueKind);
        Assert.Equal(2, to.GetArrayLength());

        IObjectOrLink back = ActivityJson.Deserialize<IObjectOrLink>(json)!;
        Assert.Equal(2, ((Note)back).To!.Count());
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsSingleValuedAsScalar()
    {
        var note = new Note
        {
            Id = "https://a.domain.local/n/4",
            To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
        };

        var json = ActivityJson.Serialize(note);

        // One recipient serializes as a JSON scalar (the library's OneOrMultipleConverter),
        // NOT as a 1-element array — this is the wire-compatibility guarantee.
        using var doc = JsonDocument.Parse(json);
        var to = doc.RootElement.GetProperty("to");
        Assert.Equal(JsonValueKind.String, to.ValueKind);
        Assert.Equal("https://www.w3.org/ns/activitystreams#Public", to.GetString());

        IObjectOrLink back = ActivityJson.Deserialize<IObjectOrLink>(json)!;
        Assert.Single(((Note)back).To!);
    }

    [Fact]
    public void Serialize_Null_Throws()
    {
        Note? note = null;

        Assert.Throws<ArgumentNullException>(() => ActivityJson.Serialize(note));
    }

    [Fact]
    public void Deserialize_EmptyJson_Throws()
    {
        Assert.Throws<JsonException>(() => ActivityJson.Deserialize<Note>(string.Empty));
    }

    [Fact]
    public void Deserialize_MalformedJson_Throws()
    {
        Assert.Throws<JsonException>(() => ActivityJson.Deserialize<Note>("{ not valid json"));
    }

    [Fact]
    public void ContentTypeConstants_AreCorrect()
    {
        Assert.Equal("application/activity+json", ActivityJson.ActivityJsonContentType);
        Assert.Equal("application/ld+json", ActivityJson.JsonLdContentType);
    }

    [Fact]
    public void Options_ExposedReadOnlyAndHasConverter()
    {
        Assert.Contains(
            ActivityJson.Options.Converters,
            c => c is KristofferStrube.ActivityStreams.JsonConverters.ObjectOrLinkConverter);
    }
}
