using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Data;

/// <summary>
/// The mapping between an ActivityStreams document and the canonical JSON string stored in a
/// <c>jsonb</c> column. All documents round-trip through <see cref="ActivityJson"/> so the concrete
/// type and the owner-only <c>privateKey</c> extension are preserved exactly as the server produces
/// them.
/// </summary>
internal static class AsDocument
{
    /// <summary>
    /// Serializes an ActivityStreams object to its canonical JSON (for storage).
    /// </summary>
    /// <param name="value">The object to serialize. Must not be null.</param>
    public static string Serialize(IObjectOrLink value) => ActivityJson.Serialize(value);

    /// <summary>
    /// Deserializes a stored JSON string into a polymorphic ActivityStreams value.
    /// </summary>
    /// <param name="json">The stored JSON. Must not be null or empty.</param>
    /// <returns>The deserialized value, or null when the JSON is empty/invalid.</returns>
    public static IObjectOrLink? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return ActivityJson.Deserialize<IObjectOrLink>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            // A corrupt payload is treated as "absent" rather than crashing the read path.
            return null;
        }
    }

    /// <summary>
    /// Extracts the <c>id</c> of an ActivityStreams value (an object's <c>Id</c>, or a link's
    /// <c>href</c>) — the IRI a store keys it by.
    /// </summary>
    public static string? IdOf(IObjectOrLink? value)
        => value is IObject obj ? obj.Id : (value as Link)?.Href?.AbsoluteUri;
}
