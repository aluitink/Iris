using System.Net;
using System.Net.Http.Headers;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Pipeline;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using KristofferStrube.ActivityStreams;

// Live ⑤ interop verification (unfollow half): reconstruct s7test's private key, sign an Undo of the
// given Follow id (arg 2), and POST it to s7test's outbox on the live instance. The server records the
// local unfollow edge and delivers the signed Undo to Lemmy's community inbox; we print the publish
// status so the operator can confirm Lemmy's community_follower row is removed.

const string s7testIri = "https://iris.luit.ink/ap/v1/u/s7test";
const string s7testKeyId = "https://iris.luit.ink/ap/v1/u/s7test#key-1";
const string lemmyCommunityIri = "https://lemmy.luit.ink/c/interop";

string keyPemPath = args.Length > 0 ? args[0] : "/tmp/iris-live/s7test_key.pem";
string mode = args.Length > 1 ? args[1] : "undo";
string followId = args.Length > 2 ? args[2] : "";

string pem = await File.ReadAllTextAsync(keyPemPath);
var key = KeyPair.FromPem(pem, KeyAlgorithm.Rsa, new Iri(s7testKeyId));

var keyStore = new InMemoryKeyStore();
keyStore.PutKey(key);
var keyProvider = new InMemoryKeyProvider(keyStore);
keyProvider.RegisterKey(new Iri(s7testIri), new Iri(s7testKeyId));
var signer = new HttpSignatureSigner(keyStore);

var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
using var handler = new HttpClientHandler();
var client = factory.Create(
    new ActivityPubClientOptions { ActorId = new Iri(s7testIri), EnableRetry = false },
    handler);

DeliveryResult result;
if (mode == "follow")
{
    Console.WriteLine($"Following: s7test -> {lemmyCommunityIri}");
    result = await client.FollowAsync(new Iri(s7testIri), new Iri(lemmyCommunityIri));
    Console.WriteLine($"Publish status: {result.StatusCode} success={result.IsSuccess}");
    Console.WriteLine($"Minted Follow id: {result.MintedId}");
}
else
{
    Console.WriteLine($"Unfollowing: s7test -> {lemmyCommunityIri} (Undo of {followId})");
    result = await client.UndoFollowAsync(new Iri(s7testIri), new Iri(followId));
    Console.WriteLine($"Publish status: {result.StatusCode} success={result.IsSuccess}");
    Console.WriteLine($"Minted Undo id: {result.MintedId}");
}
Console.WriteLine(result.Body.Length < 500 ? $"Body: {result.Body}" : $"Body (truncated): {result.Body[..500]}");
Console.WriteLine(result.IsSuccess ? "SUCCESS — published; server should deliver to Lemmy." : "PUBLISH FAILED");
