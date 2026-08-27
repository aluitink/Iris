namespace Iris.Core;

/// <summary>
/// An immutable identifier for an ActivityPub resource, wrapping an <see cref="Uri"/>.
/// This is the single identity type used across Iris; raw <see cref="string"/>/IRI values
/// are converted to and from <see cref="Iri"/> at the library boundary (see <see cref="IriExtensions"/>).
/// </summary>
/// <remarks>
/// An <see cref="Iri"/> is compared by its absolute URI (case-sensitive per RFC 3986).
/// Use <see cref="Iri.Public"/> for the public audience and the
/// <c>InboxOf</c>/<c>OutboxOf</c>/<c>FollowersOf</c>/<c>FollowingOf</c> helpers to derive
/// the standard ActivityPub collection endpoints from an actor or object IRI.
/// </remarks>
public readonly record struct Iri
{
    private readonly Uri _uri;

    /// <summary>
    /// Initializes a new <see cref="Iri"/> from a URI string.
    /// </summary>
    /// <param name="value">The IRI string. Must be non-empty and a valid absolute or relative URI.</param>
    /// <remarks>
    /// A bare path such as <c>/u/alice</c> is preserved as a *relative* IRI rather than being
    /// silently resolved to a <c>file:///u/alice</c> absolute URI (which is what the
    /// <see cref="System.Uri"/> string constructor would do). This keeps derivation helpers
    /// like <c>InboxOf</c> from accidentally producing <c>file:</c> IRIs.
    /// </remarks>
    /// <exception cref="ArgumentException">When <paramref name="value"/> is null, empty, or not parseable.</exception>
    public Iri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("IRI value must not be null or empty.", nameof(value));
        }

        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri))
        {
            throw new ArgumentException($"Not a valid IRI: '{value}'.", nameof(value));
        }

        _uri = uri;
    }

    /// <summary>
    /// Initializes a new <see cref="Iri"/> from an existing <see cref="Uri"/>.
    /// </summary>
    /// <param name="uri">The URI to wrap. Must not be null.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="uri"/> is null.</exception>
    public Iri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        _uri = uri;
    }

    /// <summary>
    /// Gets the wrapped <see cref="Uri"/>.
    /// </summary>
    public Uri Uri => _uri;

    /// <summary>
    /// Gets the IRI as an absolute URI string (the canonical wire form).
    /// </summary>
    public string Value => _uri.IsAbsoluteUri ? _uri.AbsoluteUri : _uri.ToString();

    /// <summary>
    /// The public audience IRI, <c>https://www.w3.org/ns/activitystreams#Public</c>.
    /// Used for public posts, follows, and inbox visibility.
    /// </summary>
    public static Iri Public { get; } = new("https://www.w3.org/ns/activitystreams#Public");

    /// <summary>
    /// Returns <see langword="true"/> when the IRI is absolute (has a scheme).
    /// </summary>
    public bool IsAbsolute => _uri.IsAbsoluteUri;

    /// <summary>
    /// Returns a value indicating whether this IRI is <see cref="Iri.Public"/>.
    /// </summary>
    public bool IsPublic => Equals(Public);

    /// <summary>
    /// Converts the IRI to its absolute URI string.
    /// </summary>
    public override string ToString() => Value;

    /// <summary>
    /// Parses a URI string into an <see cref="Iri"/>.
    /// </summary>
    /// <param name="value">The IRI string to parse.</param>
    /// <param name="iri">The parsed IRI, or <see langword="false"/> if the input is invalid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> parsed successfully.</returns>
    public static bool TryParse(string value, out Iri iri)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri))
        {
            iri = default;
            return false;
        }

        iri = new Iri(uri);
        return true;
    }
}
