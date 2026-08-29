using Iris.Core;

namespace Iris.Samples.SampleBlazorClient.Explorer;

/// <summary>
/// A parsed WebFinger address (the "log on to an instance" input): the actor's <em>handle</em>, the
/// instance's <em>host</em> (no scheme, no port), and the dial <see cref="Scheme"/> (defaults to
/// <c>https</c> for remote instances; the local sample instances are <c>http</c>).
/// </summary>
/// <param name="Handle">The actor's handle (e.g. <c>alice</c>).</param>
/// <param name="Host">The instance's host (e.g. <c>iris-a</c>, <c>remote.example</c>); no scheme, no
/// port. The advertised IRI host is this value (the base URL may differ — see
/// <see cref="ExplorerSession"/>) and is resolved via WebFinger on this host.</param>
/// <remarks>
/// The base URL (what the browser actually dials) is intentionally <em>not</em> part of the address:
/// a local instance's advertised host (the Docker service name) is not reachable from the browser, so
/// the host the browser dials (a host-published port) is supplied separately by the host/UI.
/// </remarks>
public sealed record WebFingerAddress(string Handle, string Host)
{
    /// <summary>
    /// The dial scheme (<c>http</c> or <c>https</c>). Defaults to <c>https</c>; local sample instances
    /// use <c>http</c>. Expressed as a property (not a positional) because <c>Scheme</c> is a C#
    /// keyword.
    /// </summary>
    public string Scheme { get; init; } = "https";

    /// <summary>
    /// Parses a WebFinger address into its (handle, host, scheme) parts. Accepted forms:
    /// <c>alice@iris-a</c>, <c>@alice@iris-a</c>, <c>alice@remote.example</c>, and the <c>acct:</c>
    /// URI form <c>acct:alice@iris-a</c> (with or without a scheme prefix). A leading <c>@</c> (the
    /// ActivityPub convention for "this handle") is optional and stripped.
    /// </summary>
    /// <param name="address">The address string. Must not be null or empty and must contain an
    /// <c>@</c> separating the handle from the host.</param>
    /// <returns>The parsed address.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="address"/> is null.</exception>
    /// <exception cref="ArgumentException">When the address is empty or not in a
    /// <c>[handle]@host</c> form (no <c>@</c>, an empty handle, or an empty host).</exception>
    public static WebFingerAddress Parse(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var value = address.Trim();

        // Strip an optional "acct:" prefix (the RFC 8410 resource URI form, e.g. "acct:alice@iris-a").
        if (value.StartsWith("acct:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["acct:".Length..];
        }

        // The host is the last segment after an '@'; the handle is everything between the first '@'
        // and the host. A leading '@' (ActivityPub "this handle") is part of the handle's prefix and
        // is stripped. This handles "@alice@iris-a" (handle=alice, host=iris-a) and "alice@iris-a".
        var lastAt = value.LastIndexOf('@');
        if (lastAt <= 0)
        {
            throw new ArgumentException(
                "A WebFinger address must be in the form 'handle@host' (e.g. 'alice@iris-a').",
                nameof(address));
        }

        var host = value[(lastAt + 1)..].Trim().TrimEnd('/');
        var handle = value[..lastAt].Trim().TrimStart('@').Trim();
        if (host.Length == 0)
        {
            throw new ArgumentException("The WebFinger address has no host.", nameof(address));
        }

        if (handle.Length == 0)
        {
            throw new ArgumentException("The WebFinger address has no handle.", nameof(address));
        }

        return new WebFingerAddress(handle, host);
    }

    /// <summary>
    /// Builds the candidate actor IRI for this address on the given base URL. The base URL is what the
    /// client dials (a host-published port for local instances); the <em>advertised</em> IRI host is
    /// the address's <see cref="Host"/> (which may differ from the dial host for local instances).
    /// </summary>
    /// <param name="dialBaseUri">The base URI the client dials (e.g.
    /// <c>http://localhost:8081</c> for a local instance published on port 8081).</param>
    /// <returns>The actor IRI <c>{scheme}://{Host}/ap/v1/u/{Handle}</c> (the advertised IRI).</returns>
    public Iri ToActorIri(Uri dialBaseUri)
    {
        ArgumentNullException.ThrowIfNull(dialBaseUri);
        return new Iri($"{Scheme}://{Host}/ap/v1/u/{Handle}");
    }

    /// <summary>
    /// Gets the <c>acct:</c> resource for this address (the WebFinger query value, e.g.
    /// <c>acct:alice@iris-a</c>).
    /// </summary>
    public string AcctResource => $"acct:{Handle}@{Host}";
}
