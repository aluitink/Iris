using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.Samples.SampleServer;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Samples.SampleBlazorClient.Tests;

/// <summary>
/// Phase 8 (second round) S7 tests: the compose screen's audience. <c>PostNoteAsync</c>'s optional
/// <c>to</c> (audience) parameter was never populated by the Compose page; the page now exposes an
/// audience input (Public or comma-separated actor IRIs) and passes it through. These tests exercise the
/// same call the page issues: post a note (and a reply) with an explicit <c>to</c> and assert the stored
/// object carries the audience (the <c>as:Public</c> address, or the given actor IRIs).
/// </summary>
public sealed class S7ComposeAudienceTests
{
    private static Uri DialBase => new("http://localhost");

    /// <summary>
    /// The ActivityStreams public collection address (the conventional public <c>to</c>).
    /// </summary>
    private static readonly Iri AsPublic = new("https://www.w3.org/ns/activitystreams#Public");

    /// <summary>
    /// Hosts a real in-process ActivityPub server with <c>alice</c> (the author) + <c>bob</c> at the dial
    /// base. Mirrors <see cref="S7ScreenTests.StartHost"/>.
    /// </summary>
    private static TestServer StartHost()
    {
        const string dialBase = "http://localhost";
        var persistence = new InMemoryPersistenceProvider();
        var aliceIri = new Iri($"{dialBase}/ap/v1/u/alice");
        var aliceKeyId = new Iri($"{aliceIri.Value}#key-1");
        var aliceKey = KeyPairGenerator.GenerateRsa(aliceKeyId);
        persistence.Keys.PutKey(aliceKey);
        var alice = new Person
        {
            Id = aliceIri.Value,
            PreferredUsername = "alice",
            Name = ["alice"],
        };
        alice.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["publicKey"] = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = aliceKeyId.Value,
                owner = aliceIri.Value,
                publicKeyPem = aliceKey.ExportPublicKeyPem(),
            }),
        };
        persistence.ActorStore.PutActorAsync(alice).GetAwaiter().GetResult();

        persistence.ActorStore.PutActorAsync(new Person
        {
            Id = $"{dialBase}/ap/v1/u/bob",
            PreferredUsername = "bob",
            Name = ["bob"],
        }).GetAwaiter().GetResult();

        var builder = new WebHostBuilder()
            .ConfigureLogging(l => { l.ClearProviders(); l.SetMinimumLevel(LogLevel.None); })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri(dialBase);
                    opts.InstanceName = "iris-a";
                    opts.InstanceActorId = aliceIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(persistence.Keys);
                s.AddSingleton<IActorDocumentFetcher>(new PersistenceActorFetcher(persistence));
                s.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
                    (_, username, password) =>
                    {
                        var valid = username == "alice"
                            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                System.Text.Encoding.UTF8.GetBytes(password),
                                System.Text.Encoding.UTF8.GetBytes(SampleServer.SampleServer.Password));
                        return new ValueTask<bool>(valid);
                    }));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }

    private sealed class PersistenceActorFetcher(IPersistenceProvider persistence) : IActorDocumentFetcher
    {
        private readonly IPersistenceProvider _persistence = persistence;

        public async Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
            => await _persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) ? actor : null;
    }

    private static async Task<(TestServer Server, IActivityPubClient Client, Iri AliceIri, Iri BobIri)> LogOnAsync()
    {
        var server = StartHost();
        var session = new ExplorerSession(() => server.CreateHandler());
        var ok = await session.LogOnAsync("alice@localhost", SampleServer.SampleServer.Password, DialBase);
        Assert.True(ok, "logon to the in-process instance must succeed");
        return (server, session.GetClient(),
            new Iri("http://localhost/ap/v1/u/alice"),
            new Iri("http://localhost/ap/v1/u/bob"));
    }

    /// <summary>
    /// Finds the stored object whose content matches <paramref name="content"/> (the note the test posted)
    /// and returns its audience (<c>to</c> hrefs).
    /// </summary>
    private static async Task<IReadOnlyList<string>> ToHrefsOfAsync(IPersistenceProvider persistence, string content)
    {
        var objects = await persistence.Objects.ListObjectsAsync();
        var posted = objects.FirstOrDefault(o => o.Content?.FirstOrDefault() == content)
            ?? throw new Xunit.Sdk.XunitException($"the posted note with content {content} must be stored");
        var to = posted is Note { To: { } noteTo }
            ? noteTo.Where(l => l is ILink { Href: { } href }).Select(l => ((ILink)l).Href!.ToString()).ToList()
            : [];
        return to;
    }

    [Fact]
    public async Task PostNote_PublicAudience_CarriesAsPublic()
    {
        var (server, client, alice, _) = await LogOnAsync();
        using var _ = server;
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();

        // The Compose page maps the audience input "Public" to the as:Public address and passes it as to.
        var content = "<p>S7: a public note from the compose screen.</p>";
        var result = await client.PostNoteAsync(alice, content, [AsPublic]);
        Assert.True(result.StatusCode == 202, $"posting a public note must succeed (got {result.StatusCode})");

        var to = await ToHrefsOfAsync(persistence, content);
        Assert.True(to.Contains(AsPublic.Value, StringComparer.Ordinal),
            $"the public note's `to` must include the as:Public address (got {string.Join(", ", to)})");
    }

    [Fact]
    public async Task PostNote_ActorAudience_CarriesActorIris()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();

        // The Compose page passes comma-separated actor IRIs through as the note's `to`.
        var content = "<p>S7: a note addressed to bob from the compose screen.</p>";
        var result = await client.PostNoteAsync(alice, content, [bob]);
        Assert.True(result.StatusCode == 202, $"posting a note addressed to bob must succeed (got {result.StatusCode})");

        var to = await ToHrefsOfAsync(persistence, content);
        Assert.True(to.Contains(bob.Value, StringComparer.Ordinal),
            $"the note's `to` must include bob's IRI (got {string.Join(", ", to)})");
    }

    [Fact]
    public async Task PostNote_NoAudience_CarriesNoTo()
    {
        var (server, client, alice, _) = await LogOnAsync();
        using var _ = server;
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();

        // No audience input (the default): the note carries no explicit `to`.
        var content = "<p>S7: an unaddressed note from the compose screen.</p>";
        var result = await client.PostNoteAsync(alice, content);
        Assert.True(result.StatusCode == 202, $"posting an unaddressed note must succeed (got {result.StatusCode})");

        var to = await ToHrefsOfAsync(persistence, content);
        Assert.True(to.Count == 0, $"the unaddressed note must carry no `to` (got {string.Join(", ", to)})");
    }

    [Fact]
    public async Task PostReply_Audience_CarriesTo()
    {
        var (server, client, alice, bob) = await LogOnAsync();
        using var _ = server;
        var persistence = server.Services.GetRequiredService<IPersistenceProvider>();

        // Seed a parent note so the reply threads under it.
        var parent = new Iri($"{alice.Value}/notes/1");
        await persistence.Objects.PutObjectAsync(new Note
        {
            Id = parent.Value,
            Content = ["<p>parent</p>"],
            AttributedTo = [new Link { Href = alice.Uri }],
        });

        // The Compose page passes the audience through for replies too.
        var content = "<p>S7: a reply addressed to bob from the compose screen.</p>";
        var result = await client.PostReplyAsync(alice, parent, content, to: [bob]);
        Assert.True(result.StatusCode == 202, $"posting an addressed reply must succeed (got {result.StatusCode})");

        var to = await ToHrefsOfAsync(persistence, content);
        Assert.True(to.Contains(bob.Value, StringComparer.Ordinal),
            $"the reply's `to` must include bob's IRI (got {string.Join(", ", to)})");
    }
}
