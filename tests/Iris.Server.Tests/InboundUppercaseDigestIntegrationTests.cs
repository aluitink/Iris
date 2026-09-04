using System.Net;
using System.Text;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 23 (22.12) — <strong>Mastodon / draft-10 <c>Digest</c> header casing on the inbound
/// server→server path</strong>: a strict Mastodon (draft-10) peer emits <c>Digest: SHA-256=&lt;b64&gt;</c>
/// with the UPPERCASE algorithm label, whereas Iris's own sender emits lowercase <c>sha-256=&lt;b64&gt;</c>.
/// The signature base embeds the <c>digest</c> header value <em>verbatim</em> (the verifier trusts the
/// declared wire value and does not recompute it), so the base the sender signs over must byte-match the
/// base the verifier reconstructs. This suite locks that guarantee end-to-end over the real signed inbox
/// pipeline (not just the crypto): an inbound server→server activity whose <c>Digest</c> header uses the
/// draft-10 uppercase wire form must be accepted (202), exactly as it would be from a live Mastodon peer.
/// </summary>
/// <remarks>
/// Topology: a single instance (digest.domain.local, alice) hosting the real signed inbox endpoint. The
/// test signs a local-actor <see cref="Create"/> with the ServerToServer profile, formatting the
/// <c>Digest</c> header value with the uppercase <c>SHA-256=</c> label (the draft-10 / Mastodon wire form
/// — the same base64, just a different label case), and asserts the inbound request is accepted (202) and
/// processed. The complement, an inbound request with Iris's own lowercase <c>sha-256=</c> form, is
/// accepted too — both round-trip, which is the whole invariant. A tampered digest (header value that
/// differs from the one the signature was computed over) is rejected (401), guarding the digest's
/// integrity-by-signature. This is the locally-testable proxy for the U-5 "a strict draft-10 sender is
/// accepted" live confirmation: the <c>Digest</c> casing is the most concrete draft-10 wire difference
/// Iris can exercise in-process.
/// </remarks>
public sealed class InboundUppercaseDigestIntegrationTests : IDisposable
{
    private const string Host = "digest.domain.local";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{Host}/ap/v1/u/{Handle}");

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly KeyPair _key;
    private readonly string _base = $"https://{Host}";

    public InboundUppercaseDigestIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _key = Seed(_persistence);
        _server = StartServer(_persistence, _key);
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false) { BaseAddress = new Uri(_base) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    // --- An inbound activity whose Digest header is the draft-10 UPPERCASE form is accepted -----

    [Fact]
    public async Task Inbox_AcceptsInboundCreate_WithUppercaseDraft10DigestHeader()
    {
        // A local-actor Create signed with the ServerToServer profile, carrying the UPPERCASE
        // `Digest: SHA-256=<b64>` value (the draft-10 / Mastodon wire form). The verifier reconstructs
        // the base from the wire (uppercase digest verbatim) and it matches the base signed over.
        var create = BuildCreate();
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync($"/ap/v1/u/{Handle}/inbox", body, digestLabelCase: DigestLabelCase.UpperCase);

        // 202 Accepted: the verifier accepted the uppercase-Digest request.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The activity was actually processed (the embedded Note is stored and served by its IRI) — not
        // just signature-accepted and dropped.
        var objectResponse = await _http.GetAsync(new Uri(create.Object!.First()!.Id!).AbsolutePath);
        objectResponse.EnsureSuccessStatusCode();
        Assert.Equal("Note", (await ParseTypeAsync(objectResponse)));
    }

    // --- The lowercase (Iris-native) form round-trips too — both casings verify -----------------

    [Fact]
    public async Task Inbox_AcceptsInboundCreate_WithLowerCaseDigestHeader()
    {
        // The complement: an inbound Create with Iris's own lowercase `Digest: sha-256=<b64>` form. Both
        // casings must round-trip (the base embeds the verbatim wire value, so case is carried through
        // faithfully on both sides). Pinned explicitly so a regression that normalizes the digest label
        // is caught for BOTH forms.
        var create = BuildCreate();
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync($"/ap/v1/u/{Handle}/inbox", body, digestLabelCase: DigestLabelCase.LowerCase);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // --- A digest value that does NOT match the signature is rejected (integrity by signature) --

    [Fact]
    public async Task Inbox_RejectsInboundCreate_WhenDigestValueIsTampered()
    {
        // The verifier trusts the declared digest value to reconstruct the base — but the SIGNATURE is
        // still checked against that reconstructed base. A request whose signature was computed over one
        // digest value but whose on-the-wire Digest header carries a different (tampered) value produces
        // a base the signature does not cover, so verification fails (401). This guards against a future
        // change that would stop checking the digest's integrity by signature.
        var create = BuildCreate();
        var body = ActivityJson.Serialize(create);
        var response = await SendSignedPostAsync(
            $"/ap/v1/u/{Handle}/inbox", body, digestLabelCase: DigestLabelCase.UpperCase, tamperDigestHeader: true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------------------

    private enum DigestLabelCase { LowerCase, UpperCase }

    private Create BuildCreate() => new()
    {
        Id = $"{ActorIri.Value}/creates/{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(ActorIri.Value) }],
        Object =
        [
            new Note
            {
                Id = $"{ActorIri.Value}/notes/{Guid.NewGuid():N}",
                Content = ["signed with a draft-10 digest"],
                AttributedTo = [new Link { Href = new Uri(ActorIri.Value) }],
            },
        ],
    };

    private static async Task<string?> ParseTypeAsync(HttpResponseMessage response)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
    }

    /// <summary>
    /// Sends a signed ServerToServer POST to the given path with the given body, formatting the
    /// <c>Digest</c> header value with the chosen algorithm-label case. The signature is computed over a
    /// base embedding that EXACT digest string (the sender signs what it puts in the header), mirroring a
    /// real peer. When <paramref name="tamperDigestHeader"/> is set, the on-the-wire <c>Digest</c> header
    /// is set to a different value than the one the signature was computed over (simulating tampering).
    /// </summary>
    private async Task<HttpResponseMessage> SendSignedPostAsync(
        string path, string body, DigestLabelCase digestLabelCase, bool tamperDigestHeader = false)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, _key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var date = DateTime.UtcNow.ToString("R");
        var contentType = ActivityJson.ActivityJsonContentType;
        var base64 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bodyBytes));
        // The draft-10 / Mastodon wire form uses the UPPERCASE algorithm label (`SHA-256=`); Iris's own
        // sender uses lowercase (`sha-256=`). The base64 is identical; only the label case differs.
        var signedDigest = digestLabelCase == DigestLabelCase.UpperCase ? $"SHA-256={base64}" : $"sha-256={base64}";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.HostHeaderName] = Host,
            [Signatures.DateHeaderName] = date,
            [Signatures.ContentTypeHeaderName] = contentType,
            [Signatures.DigestHeaderName] = signedDigest,
        };

        var metadata = new HttpRequestMetadata("POST", path, Host, date, contentType, bodyBytes, headers);
        var identity = new SystemIdentity(ActorIri, _key.KeyId);
        var signature = signer.Sign(metadata, identity, SigningProfile.ServerToServer);

        // The on-the-wire Digest header: normally the same value the signature was computed over. When
        // tampering, it is a DIFFERENT value (a different body's digest) so the reconstructed base no
        // longer matches the signature.
        var tamperedBase64 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("tampered")));
        var wireDigest = tamperDigestHeader
            ? (digestLabelCase == DigestLabelCase.UpperCase ? $"SHA-256={tamperedBase64}" : $"sha-256={tamperedBase64}")
            : signedDigest;

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base + path))
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Content.Headers.TryAddWithoutValidation(Signatures.DigestHeaderName, wireDigest);
        request.Headers.TryAddWithoutValidation(Signatures.DateHeaderName, date);
        request.Headers.TryAddWithoutValidation(Signatures.SignatureHeaderName, signature);

        return await _http.SendAsync(request);
    }

    private static KeyPair Seed(InMemoryPersistenceProvider persistence)
    {
        var keyId = new Iri($"{ActorIri.Value}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        var actor = new Person
        {
            Id = ActorIri.Value,
            PreferredUsername = Handle,
            Name = [Handle],
        };
        actor.ExtensionData ??= new Dictionary<string, System.Text.Json.JsonElement>();
        actor.ExtensionData["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = ActorIri.Value,
            publicKeyPem = key.ExportPublicKeyPem(),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        persistence.Keys.PutKey(key);
        return key;
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence, KeyPair key)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(ActorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);

        TestServer? self = null;
        var server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = Host,
            Handle = Handle,
            Persistence = persistence,
            Fetcher = BuildSelfFetcher(factory, () => self!),
        });
        self = server;
        return server;
    }

    private static IActorDocumentFetcher BuildSelfFetcher(ActivityPubClientFactory factory, Func<TestServer> selfServer)
    {
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false },
            new LazyHandler(() => selfServer().CreateHandler()));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}
