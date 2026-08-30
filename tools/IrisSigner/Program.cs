using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;

namespace Iris.Tools.IrisSigner;

/// <summary>
/// Signs an ActivityPub HTTP request as a given actor (reusing the actor's private key) and sends it
/// with the produced <c>Signature</c> + <c>Date</c> (+ <c>digest</c>/<c>content-type</c>) headers. This
/// is the Phase 8 S10 smoke test's way of driving a signed cross-container write over genuine sockets:
/// the smoke test copies this tool + the actor's private-key PEM into the home instance's container
/// (curl cannot produce an ActivityPub HTTP signature), and the tool signs the request with the same
/// signature base / headers / profile the Iris client's <c>SigningHandler</c> uses, so the receiving
/// instance validates it exactly as it would a delivery from a real peer.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
///   dotnet IrisSigner.dll &lt;method&gt; &lt;url&gt; &lt;privateKeyPemPath&gt; &lt;actorIri&gt; &lt;keyIdIri&gt; [&lt;contentType&gt;] [&lt;bodyPath&gt;]
/// </code>
/// The body (when present) is read from <c>bodyPath</c>; the content type defaults to
/// <c>application/activity+json</c> for a body-bearing request. The tool prints the raw response body
/// (with the trailing HTTP status code on a line of its own) and exits 0 on a 2xx, non-zero otherwise.
/// </remarks>
public static class Program
{
    /// <inheritdoc/>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("usage: IrisSigner <method> <url> <privateKeyPemPath> <actorIri> <keyIdIri> [contentType] [bodyPath]");
            return 2;
        }

        var method = args[0].ToUpperInvariant();
        var uri = new Uri(args[1]);
        var key = KeyPair.FromPem(File.ReadAllText(args[2]), KeyAlgorithm.Rsa, new Iri(args[4]));
        var actorIri = new Iri(args[3]);
        var contentType = args.Length > 5 ? args[5] : "application/activity+json";
        var body = args.Length > 6 ? await File.ReadAllBytesAsync(args[6]) : [];

        // Build the request exactly as the SigningHandler does: the metadata carries the (request-target)
        // path, the host, the date, the content type (when there is a body), and the digest (when there is
        // a body). The ServerToServer profile signs digest + content-type; the ClientToServer profile does
        // not (a body is never expected on the client profile, but a Follow has a body, so ServerToServer).
        var date = DateTime.UtcNow.ToString("R");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.HostHeaderName] = uri.Authority,
            [Signatures.DateHeaderName] = date,
        };
        if (body.Length > 0)
        {
            headers[Signatures.ContentTypeHeaderName] = contentType;
            headers[Signatures.DigestHeaderName] = Signatures.ComputeDigest(body);
        }
        var metadata = new HttpRequestMetadata(method, uri.PathAndQuery, uri.Authority, date,
            body.Length > 0 ? contentType : null, body, headers);

        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new Iris.Client.Auth.InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        if (!keyProvider.TryGetIdentity(actorIri, out var identity) || identity is null)
        {
            Console.Error.WriteLine("no signing identity for the actor");
            return 2;
        }
        var signer = new HttpSignatureSigner(keyStore);
        var profile = body.Length > 0 ? SigningProfile.ServerToServer : SigningProfile.ClientToServer;
        var signature = signer.Sign(metadata, identity, profile);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.TryAddWithoutValidation(Signatures.HostHeaderName, uri.Authority);
        request.Headers.TryAddWithoutValidation(Signatures.DateHeaderName, date);
        if (body.Length > 0)
        {
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation(Signatures.ContentTypeHeaderName, contentType);
            content.Headers.TryAddWithoutValidation(Signatures.DigestHeaderName, headers[Signatures.DigestHeaderName]);
            request.Content = content;
        }
        request.Headers.TryAddWithoutValidation(Signatures.SignatureHeaderName, signature);

        using var response = await http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine(responseBody);
        Console.WriteLine((int)response.StatusCode);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
}
