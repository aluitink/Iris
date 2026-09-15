using System.Text.Json;
using Iris.Core;
using Iris.Core.Collections;
using KristofferStrube.ActivityStreams;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Client.Tests;

public class LemmyPageDeserializationTests
{
    [Fact]
    public void LemmyOutboxPage_OrderedCollectionDeserialization()
    {
        var pageJson = """{"@context":["https://join-lemmy.org/context.json","https://www.w3.org/ns/activitystreams"],"type":"OrderedCollection","id":"https://lemmy.luit.ink/c/interop/outbox","totalItems":1,"orderedItems":[{"type":"Announce","id":"https://lemmy.luit.ink/activities/announce/create/51a65ff0","actor":"https://lemmy.luit.ink/c/interop","object":{"type":"Create","id":"https://lemmy.luit.ink/c/interop/123","actor":"https://lemmy.luit.ink/u/bob","published":"2024-01-01T00:00:00Z","object":{"type":"Page","id":"https://lemmy.luit.ink/post/1","name":"Hello","content":"<p>World</p>","published":"2024-01-01T00:00:00Z"}}}]}""";

        var obj = ActivityJson.Deserialize<IObject>(pageJson);
        Console.WriteLine($"[DIAG] Obj type: {obj?.GetType().FullName}");
        Console.WriteLine($"[DIAG] Is OrderedCollection: {obj is OrderedCollection}");
        Console.WriteLine($"[DIAG] Is OrderedCollectionPage: {obj is OrderedCollectionPage}");
        Console.WriteLine($"[DIAG] Is Collection: {obj is Collection}");

        if (obj is OrderedCollection oc)
        {
            Console.WriteLine($"[DIAG] OC.Id: {oc.Id}");
            Console.WriteLine($"[DIAG] OC.TotalItems: {oc.TotalItems}");
            var orderedItems = oc.OrderedItems?.ToList() ?? [];
            Console.WriteLine($"[DIAG] OC.OrderedItems count: {orderedItems.Count}");
            var items = oc.Items?.ToList() ?? [];
            Console.WriteLine($"[DIAG] OC.Items count: {items.Count}");
            
            if (orderedItems.Count > 0)
            {
                var first = orderedItems[0];
                Console.WriteLine($"[DIAG] First orderedItem type: {first.GetType().FullName}");
                Console.WriteLine($"[DIAG] First is Announce: {first is Announce}");
            }
            
            if (oc.ExtensionData is { } ext)
            {
                Console.WriteLine($"[DIAG] ExtensionData keys: {string.Join(", ", ext.Keys)}");
            }
        }

        if (obj is Collection col)
        {
            var resolvedItems = CollectionPageFactory.ResolveCollectionItems(col);
            Console.WriteLine($"[DIAG] ResolveCollectionItems count: {resolvedItems.Count}");
            if (resolvedItems.Count > 0)
            {
                var first = resolvedItems[0];
                Console.WriteLine($"[DIAG] First resolved item type: {first.GetType().FullName}");
                Console.WriteLine($"[DIAG] First is Announce: {first is Announce}");
            }
        }
    }
}
