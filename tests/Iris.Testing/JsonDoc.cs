using System.Text.Json;

namespace Iris.Testing;

/// <summary>
/// Helpers for reading the <c>items</c> property of a paged ActivityStreams collection JSON document
/// (the shape the <c>OrderedCollection</c>/<c>OrderedCollectionPage</c> endpoints emit).
/// </summary>
/// <remarks>
/// The one-or-many JSON converter renders a single item as a bare scalar/object (not an array), so
/// <see cref="GetItems"/> normalizes both the array and single-element cases. An item is either a bare
/// IRI string (a single <c>Link</c>, e.g. a member or followed actor) or an object carrying an
/// <c>id</c> (a full activity/object, e.g. a feed or search result); <see cref="ItemId"/> handles both.
/// </remarks>
public static class JsonDoc
{
    /// <summary>
    /// Normalizes the <c>items</c> property of a collection document to a list of element values (the
    /// one-or-many converter emits a single item as a scalar/object, not an array). Returns an empty
    /// list when the document has no <c>items</c> property.
    /// </summary>
    /// <param name="root">The collection document's root element.</param>
    /// <returns>The item elements (empty when there are none).</returns>
    public static List<JsonElement> GetItems(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items))
        {
            return [];
        }

        return items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray()]
            : [items];
    }

    /// <summary>
    /// Reads an item's IRI from a collection element: a bare IRI string (a single <c>Link</c>) or an
    /// object's <c>id</c> property (a full activity/object).
    /// </summary>
    /// <param name="element">The item element.</param>
    /// <returns>The item's IRI as a string.</returns>
    public static string ItemId(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : element.GetProperty("id").GetString()!;
}
