using System.Reflection;
using Iris.Core;
using KristofferStrube.ActivityStreams;

var page = new OrderedCollectionPage();
var t = typeof(OrderedCollectionPage);
Console.WriteLine("=== OrderedCollectionPage members ===");
foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
{
    Console.WriteLine($"  Property: {prop.Name} : {prop.PropertyType}");
}

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
Console.WriteLine($"\nDeserialized type: {obj?.GetType().FullName}");

if (obj is OrderedCollectionPage deserialized)
{
    var orderedProp = t.GetProperty("OrderedItems");
    var orderedVal = orderedProp?.GetValue(deserialized);
    Console.WriteLine($"OrderedItems value: {orderedVal}");
    Console.WriteLine($"OrderedItems type: {orderedVal?.GetType().FullName}");
    Console.WriteLine($"OrderedItems is null: {orderedVal is null}");

    if (orderedVal is System.Collections.IEnumerable enumerable)
    {
        int count = 0;
        foreach (var item in enumerable)
        {
            count++;
            Console.WriteLine($"  [{count}] type={item?.GetType().Name}, isIObject={item is IObject}");
        }
    }

    var itemsProp = t.GetProperty("Items");
    var itemsVal = itemsProp?.GetValue(deserialized);
    Console.WriteLine($"\nItems value: {itemsVal}");
    Console.WriteLine($"Items is null: {itemsVal is null}");
}
