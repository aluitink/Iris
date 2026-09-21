using System.Text.Json;
using Iris.Core;
using KristofferStrube.ActivityStreams;

var json = """
{
  "id": "http://localhost:8088/ap/v1/u/test/feed?page=1",
  "type": "OrderedCollectionPage",
  "orderedItems": [
    {
      "id": "http://localhost:8088/ap/v1/u/test/notes/1",
      "type": "Note",
      "attributedTo": "http://localhost:8088/ap/v1/u/test",
      "to": ["as:Public"],
      "content": "hello world"
    }
  ],
  "totalItems": 1,
  "first": "http://localhost:8088/ap/v1/u/test/feed"
}
""";

var obj = ActivityJson.Deserialize<IObjectOrLink>(json);
Console.WriteLine($"Type: {obj?.GetType().Name}");

if (obj is OrderedCollectionPage page)
{
    var ordered = page.GetOrderedItems();
    Console.WriteLine($"OrderedItems count: {ordered?.Count ?? -1}");
    var itemsProp = page.GetItems();
    Console.WriteLine($"Items count: {itemsProp?.Count ?? -1}");
    if (ordered is not null)
    {
        foreach (var item in ordered)
        {
            Console.WriteLine($"  Item runtime type: {item.GetType().Name}, is IObject: {item is IObject}");
        }
    }
}
else
{
    Console.WriteLine($"Not an OrderedCollectionPage. Actual: {obj?.GetType().Name}");
    var reserialized = JsonSerializer.Serialize(obj, typeof(IObjectOrLink), ActivityJson.Options);
    Console.WriteLine($"Reserialized: {reserialized[..Math.Min(500, reserialized.Length)]}");
}
