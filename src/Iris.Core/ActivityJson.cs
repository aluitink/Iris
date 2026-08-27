using System.Text.Json;
using System.Text.Json.Serialization;
using KristofferStrube.ActivityStreams.JsonConverters;

namespace Iris.Core;

/// <summary>
/// The single, pre-configured entry point for serializing and deserializing ActivityStreams /
/// ActivityPub types. All JSON in Iris goes through this class so that the
/// <c>@context</c>, content type, and polymorphic converters are consistent across the codebase.
/// </summary>
/// <remarks>
/// Do not call <see cref="JsonSerializer"/> with default options for ActivityStreams types —
/// that bypasses the polymorphic <c>IObjectOrLink</c> converter and the library's
/// one-or-multiple / link converters. Always use <see cref="Serialize{T}(T)"/> and
/// <see cref="Deserialize{T}(string)"/>.
/// </remarks>
public static class ActivityJson
{
    /// <summary>
    /// The <c>application/activity+json</c> media type — the content type Iris produces and prefers.
    /// </summary>
    public const string ActivityJsonContentType = "application/activity+json";

    /// <summary>
    /// The <c>application/ld+json</c> media type — accepted on inbound requests for leniency.
    /// </summary>
    public const string JsonLdContentType = "application/ld+json";

    private static readonly JsonSerializerOptions _options = CreateOptions();

    /// <summary>
    /// Gets the pre-configured <see cref="JsonSerializerOptions"/> used by this class.
    /// Exposed read-only so callers can build derived options without re-registering converters.
    /// </summary>
    public static JsonSerializerOptions Options => _options;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            // Omit unset (null/default) properties so wire payloads stay minimal.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            // ActivityStreams terms are lower-camel (id, type, @context); the library
            // properties are PascalCase, so no naming policy is applied — the library's
            // [JsonPropertyName] attributes already pin the exact term names.
            PropertyNamingPolicy = null,
            // Preserve property order as declared (stable, readable wire format).
            PropertyNameCaseInsensitive = false,
        };

        // The polymorphic converter dispatches on the "type" property to the concrete
        // ActivityStreams type and is the root of all (de)serialization.
        options.Converters.Add(new ObjectOrLinkConverter());
        return options;
    }

    /// <summary>
    /// Serializes an ActivityStreams value to JSON using the configured options.
    /// </summary>
    /// <typeparam name="T">The static type of the value (e.g. <c>IObjectOrLink</c>, <c>Person</c>).</typeparam>
    /// <param name="value">The value to serialize. Must not be null.</param>
    /// <returns>The JSON string, with the ActivityStreams <c>@context</c> included.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="value"/> is null.</exception>
    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, value.GetType(), _options);
    }

    /// <summary>
    /// Serializes an ActivityStreams value to JSON, writing to the specified <see cref="Utf8JsonWriter"/>.
    /// </summary>
    /// <typeparam name="T">The static type of the value.</typeparam>
    /// <param name="value">The value to serialize. Must not be null.</param>
    /// <param name="writer">The writer to write to. Must not be null.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="value"/> or <paramref name="writer"/> is null.</exception>
    public static void Serialize<T>(T value, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(writer);
        JsonSerializer.Serialize(writer, value, value.GetType(), _options);
    }

    /// <summary>
    /// Deserializes JSON into the requested ActivityStreams type using the configured options.
    /// </summary>
    /// <typeparam name="T">
    /// The target type. For polymorphic payloads deserialize into <c>IObjectOrLink</c> (or
    /// <c>IObject</c>/<c>ILink</c>) and then pattern-match — never into a concrete type.
    /// </typeparam>
    /// <param name="json">The JSON to deserialize. Must not be null.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">When <paramref name="json"/> is malformed.</exception>
    public static T? Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<T>(json, _options);
    }

    /// <summary>
    /// Deserializes a UTF-8 JSON payload into the requested ActivityStreams type.
    /// </summary>
    /// <typeparam name="T">The target type (see <see cref="Deserialize{T}(string)"/>).</typeparam>
    /// <param name="utf8Json">The UTF-8 JSON bytes as a span.</param>
    /// <returns>The deserialized value.</returns>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
        => JsonSerializer.Deserialize<T>(utf8Json, _options);
}
