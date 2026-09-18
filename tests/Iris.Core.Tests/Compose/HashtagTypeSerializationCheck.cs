using System.Collections.Generic;
using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using AO = KristofferStrube.ActivityStreams.Object;
using Xunit;

namespace Iris.Core.Tests.Compose;

/// <summary>
/// 54.14 — documents how the write path serializes a <c>Hashtag</c> tag: an <see cref="AO"/> built with
/// <c>Type = ["Hashtag"]</c> serializes its <c>type</c> as the bare STRING <c>"Hashtag"</c> (the
/// ActivityStreams library renders a single-element <c>Type</c> list as a plain string, not an array).
/// That string form is what the object store's <c>Deserialize&lt;IObjectOrLink&gt;</c> reconstructs
/// correctly on read (see <see cref="HashtagStoreRoundTripTests"/>).
/// </summary>
public class HashtagTypeSerializationCheck
{
    [Fact]
    public void Hashtag_Serializes_Type_AsBareString()
    {
        var hashtag = new AO { Type = ["Hashtag"], Name = ["#test"] };
        hashtag.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["href"] = JsonSerializer.SerializeToElement("https://x/search?q=%23test"),
        };

        var json = ActivityJson.Serialize(hashtag);

        // The type is the bare string "Hashtag" (not an array) — a single-element Type list renders as
        // a plain string. This is the shape the write path stores and the read path reconstructs.
        using var doc = JsonDocument.Parse(json);
        var typeEl = doc.RootElement.GetProperty("type");
        Assert.Equal(JsonValueKind.String, typeEl.ValueKind);
        Assert.Equal("Hashtag", typeEl.GetString());
        // A single-element Name list also serializes as a bare string (the library's compact form).
        var nameEl = doc.RootElement.GetProperty("name");
        var name = nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : nameEl.EnumerateArray().Single().GetString();
        Assert.Equal("#test", name);
        Assert.Equal("https://x/search?q=%23test", doc.RootElement.GetProperty("href").GetString());
    }
}
