using System.Text.Json;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Testing;

/// <summary>
/// Seeds an <see cref="InMemoryPersistenceProvider"/> with test actors, communities, memberships,
/// and outbox content. These are the per-test federation/endpoint helpers that were previously
/// copy-pasted across the <c>Iris.Server.Tests</c> integration suites; they now live in one place so
/// every suite seeds identically.
/// </summary>
/// <remarks>
/// The seeding methods are synchronous and call the store's async methods with
/// <c>GetAwaiter().GetResult()</c>: the in-memory store completes synchronously, so this is safe in a
/// test context (no async-over-async lock). All IRIs follow the standard
/// <c>https://{host}/ap/v1/u/{handle}</c> (person) and <c>https://{host}/ap/v1/c/{name}</c> (community)
/// conventions the endpoints expect.
/// </remarks>
public static class TestSeeder
{
    /// <summary>
    /// Seeds a <see cref="Person"/> actor under its standard IRI. Idempotent (re-seeding replaces).
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="host">The instance hostname (e.g. <c>a.domain.local</c>).</param>
    /// <param name="handle">The actor's handle (e.g. <c>alice</c>).</param>
    /// <returns>The actor's IRI.</returns>
    public static Iri SeedPerson(InMemoryPersistenceProvider persistence, string host, string handle)
    {
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        }).GetAwaiter().GetResult();
        return actorIri;
    }

    /// <summary>
    /// Seeds a <see cref="Person"/> actor together with a real RSA-2048 signing key, storing the key in
    /// the provider's <see cref="IPersistenceProvider.Keys"/> and serving the key's public key as PEM
    /// (<c>publicKeyPem</c>) in the actor's <c>publicKey</c> extension — the most widely compatible
    /// wire format — so a remote resolver can verify signatures. Idempotent (re-seeding replaces the
    /// actor and key).
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="host">The instance hostname (e.g. <c>a.domain.local</c>).</param>
    /// <param name="handle">The actor's handle (e.g. <c>alice</c>).</param>
    /// <returns>The seeded key, the actor's IRI, and the key's IRI (<c>{actorIri}#key-1</c>).</returns>
    public static (KeyPair Key, Iri ActorIri, Iri KeyId) SeedPersonWithKey(
        InMemoryPersistenceProvider persistence, string host, string handle)
    {
        var actorIriString = $"https://{host}/ap/v1/u/{handle}";
        var actorIri = new Iri(actorIriString);
        var keyId = new Iri($"{actorIriString}#key-1");

        var key = KeyPairGenerator.GenerateRsa(keyId);
        persistence.Keys.PutKey(key);

        var actor = new Person
        {
            Id = actorIriString,
            PreferredUsername = handle,
            Name = [handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = actorIriString,
            publicKeyPem = key.ExportPublicKeyPem(),
        });
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();

        return (key, actorIri, keyId);
    }

    /// <summary>
    /// Seeds a <see cref="Person"/> actor with <c>manuallyApprovesFollowers</c> set (in the actor's
    /// <c>ExtensionData</c>, the library-untyped property). An inbound follow of such an actor is
    /// <em>not</em> auto-accepted — the operator responds with an explicit
    /// <c>Accept</c>/<c>Reject</c> (Resolved Decision #46). Idempotent (re-seeding replaces).
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="host">The instance hostname (e.g. <c>a.domain.local</c>).</param>
    /// <param name="handle">The actor's handle (e.g. <c>alice</c>).</param>
    /// <returns>The actor's IRI.</returns>
    public static Iri SeedManuallyApprovingPerson(InMemoryPersistenceProvider persistence, string host, string handle)
    {
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
        };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData[Iris.Server.ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName] =
            JsonDocument.Parse("true").RootElement.Clone();
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        return actorIri;
    }

    /// <summary>
    /// Seeds a <see cref="Group"/> community under its standard IRI. Idempotent (re-seeding replaces).
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="host">The instance hostname (e.g. <c>a.domain.local</c>).</param>
    /// <param name="name">The community's name/handle (e.g. <c>iris</c>).</param>
    /// <returns>The community's IRI.</returns>
    public static Iri SeedCommunity(InMemoryPersistenceProvider persistence, string host, string name)
    {
        var communityIri = new Iri($"https://{host}/ap/v1/c/{name}");
        persistence.Communities.PutCommunityAsync(new Group
        {
            Id = communityIri.Value,
            PreferredUsername = name,
            Name = [name],
        }).GetAwaiter().GetResult();
        return communityIri;
    }

    /// <summary>
    /// Seeds a <see cref="Group"/> community together with a real RSA-2048 signing key, storing the key
    /// in the provider's <see cref="IPersistenceProvider.Keys"/> and serving the key's public key as PEM
    /// (<c>publicKeyPem</c>) in the community's <c>publicKey</c> extension (so a remote resolver can
    /// verify signatures the community signs). An optional local member can be recorded. Idempotent
    /// (re-seeding replaces).
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="host">The instance hostname (e.g. <c>a.domain.local</c>).</param>
    /// <param name="name">The community's name/handle (e.g. <c>iris</c>).</param>
    /// <param name="memberIri">The local actor IRI to record as a member (optional).</param>
    /// <returns>The community's key (for outbound signing), the community's IRI, and the key's IRI.</returns>
    public static (KeyPair Key, Iri CommunityIri, Iri KeyId) SeedCommunityWithKey(
        InMemoryPersistenceProvider persistence, string host, string name, Iri? memberIri = null)
    {
        var communityIri = new Iri($"https://{host}/ap/v1/c/{name}");
        var keyId = new Iri($"{communityIri.Value}#key-1");
        var key = KeyPairGenerator.GenerateRsa(keyId);
        persistence.Keys.PutKey(key);

        var community = new Group
        {
            Id = communityIri.Value,
            PreferredUsername = name,
            Name = [name],
        };
        community.ExtensionData ??= new Dictionary<string, JsonElement>();
        community.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = keyId.Value,
            owner = communityIri.Value,
            publicKeyPem = key.ExportPublicKeyPem(),
        });
        persistence.Communities.PutCommunityAsync(community).GetAwaiter().GetResult();

        if (memberIri is not null)
        {
            persistence.Communities.AddMemberAsync(communityIri, memberIri.Value).GetAwaiter().GetResult();
        }

        return (key, communityIri, keyId);
    }

    /// <summary>
    /// Seeds a <see cref="Person"/> actor that advertises an <c>endpoints.sharedInbox</c> (F-01) — the
    /// shape a remote instance's actor document takes when it exposes a shared inbox for its actors. The
    /// actor carries no signing key; it is a delivery <em>target</em> (its inbox / shared inbox is where a
    /// remote sender posts). Idempotent (re-seeding replaces).
    /// </summary>
    /// <param name="persistence">The persistence provider to seed.</param>
    /// <param name="host">The instance hostname (e.g. <c>b.domain.local</c>).</param>
    /// <param name="handle">The actor's handle (e.g. <c>bob</c>).</param>
    /// <param name="sharedInbox">The shared inbox IRI to advertise in <c>endpoints.sharedInbox</c>.</param>
    /// <returns>The actor's IRI.</returns>
    public static Iri SeedPersonWithSharedInbox(
        InMemoryPersistenceProvider persistence, string host, string handle, Iri sharedInbox)
    {
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        var actor = new Person
        {
            Id = actorIri.Value,
            PreferredUsername = handle,
            Name = [handle],
            Endpoints = new Endpoints { SharedInbox = sharedInbox.Uri },
        };
        persistence.ActorStore.PutActorAsync(actor).GetAwaiter().GetResult();
        return actorIri;
    }

    /// <summary>
    /// Adds <paramref name="memberIri"/> as a member of <paramref name="communityIri"/> (idempotent).
    /// </summary>
    /// <param name="persistence">The persistence provider to update.</param>
    /// <param name="communityIri">The community to add the member to.</param>
    /// <param name="memberIri">The local actor IRI to add as a member.</param>
    public static void AddMember(
        InMemoryPersistenceProvider persistence, Iri communityIri, Iri memberIri)
        => persistence.Communities.AddMemberAsync(communityIri, memberIri).GetAwaiter().GetResult();

    /// <summary>
    /// Appends a <see cref="Create"/> activity (wrapping a <see cref="Note"/>) to the actor's outbox.
    /// Outbox order is insertion order; the feed surfaces it newest-first.
    /// </summary>
    /// <param name="persistence">The persistence provider to update.</param>
    /// <param name="actorIri">The actor whose outbox the activity is added to.</param>
    /// <param name="activityId">The activity's IRI (unique per outbox).</param>
    /// <param name="content">The note's text content.</param>
    public static void AddCreateActivity(
        InMemoryPersistenceProvider persistence, Iri actorIri, string activityId, string content)
    {
        persistence.Activities.AddToOutboxAsync(actorIri, new Create
        {
            Id = activityId,
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Note { Id = $"{activityId}#note", Content = [content] }],
        }).GetAwaiter().GetResult();
    }
}
