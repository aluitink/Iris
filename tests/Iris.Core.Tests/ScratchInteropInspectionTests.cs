using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Core.Tests;

/// <summary>
/// Scratch test to inspect how the library deserializes unknown/foreign ActivityStreams types
/// (PeerTube Video, Pleroma toot) into Iris's polymorphic model.
/// </summary>
public class ScratchInteropInspectionTests
{
    [Fact]
    public void Inspect_VideoType_Deserialization()
    {
        var json = """
            {
              "@context": ["https://join-lemmy.org/context.json", "https://www.w3.org/ns/activitystreams"],
              "type": "Video",
              "id": "https://peertube.example.org/videos/watch/abc",
              "name": "A PeerTube video",
              "content": "<p>Video description</p>",
              "duration": "PT10M",
              "url": "https://peertube.example.org/static/files/abc.webm",
              "preview": "https://peertube.example.org/thumbnails/abc.jpg",
              "attributedTo": "https://peertube.example.org/users/foo",
              "published": "2024-01-01T00:00:00Z"
            }
            """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;
        Console.WriteLine($"Video type -> {payload.GetType().Name}");
        if (payload is IObject obj)
        {
            Console.WriteLine($"  Id: {obj.Id}");
            Console.WriteLine($"  Name: {string.Join(",", obj.Name ?? [])}");
            Console.WriteLine($"  Content: {string.Join(",", obj.Content ?? [])}");
            Console.WriteLine($"  ExtensionData count: {obj.ExtensionData?.Count ?? 0}");
            if (obj.ExtensionData is { } ext)
            {
                foreach (var kv in ext)
                {
                    Console.WriteLine($"  ext[{kv.Key}] = {kv.Value.GetRawText()}");
                }
            }
        }
        // Round-trip
        var reserialized = ActivityJson.Serialize(payload);
        using var doc = JsonDocument.Parse(reserialized);
        Console.WriteLine($"  Round-trip type: {doc.RootElement.GetProperty("type").GetString()}");
        Assert.NotNull(payload);
    }

    [Fact]
    public void Inspect_PleromaNote_Deserialization()
    {
        var json = """
            {
              "@context": ["https://join-lemmy.org/context.json", "https://www.w3.org/ns/activitystreams"],
              "type": "Note",
              "id": "https://pleroma.example.org/objects/xyz",
              "content": "<p>hi</p>",
              "source": {"content": "hi", "mediaType": "text/markdown"},
              "conversationId": "https://pleroma.example.org/objects/root",
              "emoji": [{"name": "cat", "imageUrl": "https://pleroma.example.org/emoji/cat.png", "shortCode": ":cat:"}],
              "toot": {"emoji": []},
              "pleroma": {"local": true},
              "attributedTo": "https://pleroma.example.org/users/foo"
            }
            """;

        IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json)!;
        Console.WriteLine($"Pleroma Note type -> {payload.GetType().Name}");
        if (payload is IObject obj)
        {
            Console.WriteLine($"  ExtensionData count: {obj.ExtensionData?.Count ?? 0}");
            if (obj.ExtensionData is { } ext)
            {
                foreach (var kv in ext)
                {
                    Console.WriteLine($"  ext[{kv.Key}] = {kv.Value.GetRawText()}");
                }
            }
        }
        var reserialized = ActivityJson.Serialize(payload);
        Console.WriteLine($"  Round-trip contains conversationId: {reserialized.Contains("conversationId")}");
        Console.WriteLine($"  Round-trip contains pleroma: {reserialized.Contains("pleroma")}");
        Assert.NotNull(payload);
    }
}

public class ScratchVideoTypeTests
{
    [Fact]
    public void Inspect_VideoTypeProperties()
    {
        var asm = typeof(KristofferStrube.ActivityStreams.Object).Assembly;
        var videoType = asm.GetTypes().First(t => t.Name == "Video");
        Console.WriteLine($"Video type: {videoType.FullName}");
        Console.WriteLine("Properties:");
        foreach (var p in videoType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
        {
            Console.WriteLine($"  DECLARED {p.PropertyType.Name} {p.Name}");
        }
        foreach (var p in videoType.BaseType!.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
        {
            Console.WriteLine($"  BASE {p.PropertyType.Name} {p.Name}");
        }
    }
}
