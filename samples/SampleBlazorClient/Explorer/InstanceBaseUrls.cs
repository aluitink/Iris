using System.Net.Http;

namespace Iris.Samples.SampleBlazorClient.Explorer;

/// <summary>
/// The instance base-URL config surface (SAMPLE_PLAN §4.4): the mapping from an instance's advertised
/// <em>host</em> (the host in a WebFinger address, e.g. <c>iris-a</c>) to the browser-reachable base URL
/// the explorer dials (e.g. <c>http://localhost:8081</c>). This is the heart of the "Docker-only
/// routable" rule: the IRIs in documents carry the advertised host (which Docker DNS resolves between
/// containers), but the browser — outside the Docker network — dials a host-published port. The UI uses
/// this map to pre-fill the base URL for a known local instance so the user only enters the WebFinger
/// address and password.
/// </summary>
/// <remarks>
/// The map is an <see cref="IReadOnlyDictionary{TKey,TValue}"/> keyed by the advertised host
/// (lower-cased; hostnames are case-insensitive). An empty map is valid (a user-supplied base URL is
/// always accepted at logon); a null/empty entry for a host means "no known browser base URL" and the
/// UI falls back to its own default.
/// </remarks>
public sealed class InstanceBaseUrls
{
    private readonly Dictionary<string, Uri> _map;

    /// <summary>
    /// Initializes a new instance base-URL map.
    /// </summary>
    /// <param name="entries">
    /// The host → browser base URL pairs to seed the map with (e.g.
    /// <c>{ "iris-a", new Uri("http://localhost:8081") }</c>). May be null or empty.
    /// </param>
    public InstanceBaseUrls(IEnumerable<KeyValuePair<string, Uri>>? entries = null)
    {
        _map = new(StringComparer.OrdinalIgnoreCase);
        if (entries is not null)
        {
            foreach (var (host, baseUri) in entries)
            {
                Set(host, baseUri);
            }
        }
    }

    /// <summary>
    /// Gets the number of known instances.
    /// </summary>
    public int Count => _map.Count;

    /// <summary>
    /// Gets a snapshot of the known instance hosts (for a UI's instance picker).
    /// </summary>
    public IReadOnlyCollection<string> Hosts => (IReadOnlyCollection<string>)_map.Keys;

    /// <summary>
    /// Tries to get the browser base URL for a known instance host.
    /// </summary>
    /// <param name="host">The instance's advertised host (e.g. <c>iris-a</c>).</param>
    /// <param name="baseUri">The browser base URL, when the host is known.</param>
    /// <returns><see langword="true"/> when the host has a known base URL; otherwise
    /// <see langword="false"/>.</returns>
    public bool TryGet(string host, out Uri baseUri)
    {
        if (!string.IsNullOrWhiteSpace(host) && _map.TryGetValue(host.Trim(), out var uri))
        {
            baseUri = uri;
            return true;
        }

        baseUri = default!;
        return false;
    }

    /// <summary>
    /// Gets the browser base URL for a known instance host, or <see langword="null"/> when unknown.
    /// </summary>
    /// <param name="host">The instance's advertised host.</param>
    public Uri? this[string host] => TryGet(host, out var uri) ? uri : null;

    /// <summary>
    /// Records (or replaces) the browser base URL for an instance host.
    /// </summary>
    /// <param name="host">The instance's advertised host.</param>
    /// <param name="baseUri">The browser-reachable base URL the explorer dials for this host.</param>
    public void Set(string host, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("The instance host must not be empty.", nameof(host));
        }

        _map[host.Trim()] = baseUri;
    }
}
