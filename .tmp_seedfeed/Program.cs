using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Collections;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Microsoft.Extensions.Logging;
using KristofferStrube.ActivityStreams;

// S36 live evidence capture: sign as ii-a1 (A) and ii-b1 (B), fetch the live follow feeds,
// and dump item-by-item IRI + type + content-object IRI + embedded flag.
var pemA = File.ReadAllText("/tmp/qa-key-ii-a1.pem");
var pemB = File.ReadAllText("/tmp/qa-key-ii-b1.pem");

var iiA1 = new Iri("https://qa-iris-a.luit.ink/ap/v1/u/ii-a1");
var iiB1 = new Iri("https://qa-iris-b.luit.ink/ap/v1/u/ii-b1");

var store = new InMemoryKeyStore();
var keyA1 = KeyPair.FromPem(pemA, KeyAlgorithm.Rsa, new Iri($"{iiA1.Value}#key-1"));
var keyB1 = KeyPair.FromPem(pemB, KeyAlgorithm.Rsa, new Iri($"{iiB1.Value}#key-1"));
store.PutKey(keyA1);
store.PutKey(keyB1);
var provider = new InMemoryKeyProvider(store);
provider.RegisterKey(iiA1, keyA1.KeyId);
provider.RegisterKey(iiB1, keyB1.KeyId);
var signer = new HttpSignatureSigner(store);
var factory = new ActivityPubClientFactory(store, provider, signer);

using var handlerA = new HttpClientHandler();
using var handlerB = new HttpClientHandler();
var clientA1 = factory.Create(new ActivityPubClientOptions { ActorId = iiA1, EnableRetry = false }, handlerA);
var clientB1 = factory.Create(new ActivityPubClientOptions { ActorId = iiB1, EnableRetry = false }, handlerB);

async Task DumpAsync(string label, IActivityPubClient client, Iri actor)
{
    Console.WriteLine($"=== {label} follow feed (signed as {actor.Value}) ===");
    var n = 0;
    await foreach (var item in client.GetFollowFeedAsync(actor, new CollectionQuery { Limit = 200 }))
    {
        n++;
        var desc = Describe(item);
        Console.WriteLine($"  [{n}] {desc}");
    }
    Console.WriteLine($"total: {n}");
}

static string Describe(IObjectOrLink item)
{
    if (item is Activity act)
    {
        var obj = act.Object?.FirstOrDefault();
        var objIri = obj?.ResolveObjectIri()?.Value ?? "(none)";
        var embedded = obj is IObject;
        return $"{act.Type?.FirstOrDefault()} act={act.Id} obj={objIri} embedded={embedded}";
    }

    if (item is IObject o)
    {
        return $"{o.Type?.FirstOrDefault()} obj={o.Id}";
    }

    return item.ToString() ?? "?";
}

await DumpAsync("ii-a1 (A)", clientA1, iiA1);
await DumpAsync("ii-b1 (B)", clientB1, iiB1);
