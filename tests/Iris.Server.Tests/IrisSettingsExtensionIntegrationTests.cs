using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Object = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 22.6.1 integration tests: the <c>iris:settings</c> JSON-LD extension property is advertised on
/// the public actor/community document when the actor/community has an AP-native settings gate
/// (<c>manuallyApprovesFollowers</c> for a person, <c>manuallyApprovesMembers</c> for a community). The
/// extension carries the IRI of the settings surface (the actor/community's outbox — where AP-native
/// <c>Add</c>/<c>Remove</c> settings activities are published). The <c>iris:capabilities</c> list also
/// includes <c>"settings"</c> when the gate is present. A client reading the document via
/// <see cref="IrisDocumentExtensions.GetSettingsIri(Object, string)"/> can discover the settings surface
/// from the document alone (no hardcoded endpoint paths).
/// </summary>
public sealed class IrisSettingsExtensionIntegrationTests : IDisposable
{
    private const string Host = "a.domain.local";
    private const string Handle = "alice";
    private const string Password = "s3cret!";
    private const string CarolHandle = "carol";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly InMemoryPersistenceProvider _persistence;

    public IrisSettingsExtensionIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        Seed(_persistence);

        var builder = new WebHostBuilder()
            .ConfigureLogging(l => { l.ClearProviders(); l.SetMinimumLevel(LogLevel.None); })
            .ConfigureServices(s =>
            {
                s.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{Host}");
                    opts.InstanceName = "test-iris";
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(_persistence);
                s.AddSingleton<IActorCredentialValidator>(new BasicAuthCredentialValidator(
                    (iri, username, password) =>
                    {
                        var expected = new Iri($"https://{Host}/ap/v1/u/{Handle}");
                        var valid = iri == expected &&
                            username == Handle &&
                            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                System.Text.Encoding.UTF8.GetBytes(password),
                                System.Text.Encoding.UTF8.GetBytes(Password));
                        return new ValueTask<bool>(valid);
                    }));
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        _server = new TestServer(builder);
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static void Seed(InMemoryPersistenceProvider persistence)
    {
        // Alice: a normal person (no manuallyApprovesFollowers).
        var aliceIri = $"https://{Host}/ap/v1/u/{Handle}";
        var aliceKey = KeyPairGenerator.GenerateRsa(new Iri($"{aliceIri}#key-1"));
        persistence.Keys.PutKey(aliceKey);
        var alice = new Person
        {
            Id = aliceIri,
            PreferredUsername = Handle,
            Name = [Handle],
        };
        alice.ExtensionData ??= new Dictionary<string, JsonElement>();
        alice.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = aliceIri + "#key-1",
            owner = aliceIri,
            publicKeyPem = aliceKey.ExportPublicKeyPem(),
        });
        persistence.ActorStore.PutActorAsync(alice).GetAwaiter().GetResult();

        // Carol: a person WITH manuallyApprovesFollowers set (the settings gate).
        var carolIri = $"https://{Host}/ap/v1/u/{CarolHandle}";
        var carolKey = KeyPairGenerator.GenerateRsa(new Iri($"{carolIri}#key-1"));
        persistence.Keys.PutKey(carolKey);
        var carol = new Person
        {
            Id = carolIri,
            PreferredUsername = CarolHandle,
            Name = [CarolHandle],
        };
        carol.ExtensionData ??= new Dictionary<string, JsonElement>();
        carol.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id = carolIri + "#key-1",
            owner = carolIri,
            publicKeyPem = carolKey.ExportPublicKeyPem(),
        });
        carol.ExtensionData[ActivityPubServerConstants.ManuallyApprovesFollowersExtensionName] =
            JsonDocument.Parse("true").RootElement.Clone();
        persistence.ActorStore.PutActorAsync(carol).GetAwaiter().GetResult();

        // "devs": a community WITH manuallyApprovesMembers set (the settings gate).
        var devsIri = $"https://{Host}/ap/v1/c/devs";
        var devsKey = KeyPairGenerator.GenerateRsa(new Iri($"{devsIri}#key-1"));
        persistence.Communities.PutCommunityAsync(new Group
        {
            Id = devsIri,
            PreferredUsername = "devs",
            Name = ["Devs"],
            ExtensionData = new Dictionary<string, JsonElement>
            {
                [ActivityPubServerConstants.ManuallyApprovesMembersExtensionName] = JsonDocument.Parse("true").RootElement.Clone(),
            },
        }).GetAwaiter().GetResult();

        // "open": a community WITHOUT the gate (no settings surface).
        var openIri = $"https://{Host}/ap/v1/c/open";
        var openKey = KeyPairGenerator.GenerateRsa(new Iri($"{openIri}#key-1"));
        persistence.Communities.PutCommunityAsync(new Group
        {
            Id = openIri,
            PreferredUsername = "open",
            Name = ["Open"],
        }).GetAwaiter().GetResult();
    }

    // --- Person document: iris:settings present when manuallyApprovesFollowers is set ----------

    [Fact]
    public async Task PersonDoc_ManuallyApprovesFollowers_AdvertisesSettingsExtension()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/ap/v1/u/{CarolHandle}");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // The iris:settings extension must be present with the outbox IRI.
        var settingsTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + ActivityPubServerConstants.SettingsTerm;
        Assert.True(
            doc.RootElement.TryGetProperty(settingsTerm, out var settings),
            $"the person document must advertise the {settingsTerm} extension (22.6.1)");
        Assert.Equal(JsonValueKind.String, settings.ValueKind);

        var expectedOutbox = $"https://{Host}/ap/v1/u/{CarolHandle}/outbox";
        Assert.Equal(expectedOutbox, settings.GetString());

        // The iris:capabilities list must include "settings".
        var capabilitiesTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + ActivityPubServerConstants.CapabilitiesTerm;
        Assert.True(
            doc.RootElement.TryGetProperty(capabilitiesTerm, out var capabilities),
            $"the person document must advertise the {capabilitiesTerm} extension (22.6.1)");
        Assert.Equal(JsonValueKind.Array, capabilities.ValueKind);
        var values = capabilities.EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(ActivityPubServerConstants.CapabilitySettings, values);
    }

    // --- Person document: iris:settings absent when no gate --------------------------------------

    [Fact]
    public async Task PersonDoc_NoGate_DoesNotAdvertiseSettingsExtension()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/ap/v1/u/{Handle}");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // The iris:settings extension must be ABSENT (alice has no manuallyApprovesFollowers).
        var settingsTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + ActivityPubServerConstants.SettingsTerm;
        Assert.False(
            doc.RootElement.TryGetProperty(settingsTerm, out _),
            "the person document must NOT advertise the iris:settings extension when no gate is set");

        // The iris:capabilities list must NOT include "settings".
        var capabilitiesTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + ActivityPubServerConstants.CapabilitiesTerm;
        Assert.True(
            doc.RootElement.TryGetProperty(capabilitiesTerm, out var capabilities),
            "the person document must advertise the iris:capabilities extension");
        var values = capabilities.EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.DoesNotContain(ActivityPubServerConstants.CapabilitySettings, values);
    }

    // --- Community document: iris:settings present when manuallyApprovesMembers is set ----------

    [Fact]
    public async Task CommunityDoc_ManuallyApprovesMembers_AdvertisesSettingsExtension()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/ap/v1/c/devs");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // The iris:settings extension must be present with the outbox IRI.
        var settingsTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + ActivityPubServerConstants.SettingsTerm;
        Assert.True(
            doc.RootElement.TryGetProperty(settingsTerm, out var settings),
            $"the community document must advertise the {settingsTerm} extension (22.6.1)");
        Assert.Equal(JsonValueKind.String, settings.ValueKind);

        var expectedOutbox = $"https://{Host}/ap/v1/c/devs/outbox";
        Assert.Equal(expectedOutbox, settings.GetString());

        // The iris:capabilities list must include "settings".
        var capabilitiesTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + ActivityPubServerConstants.CapabilitiesTerm;
        Assert.True(
            doc.RootElement.TryGetProperty(capabilitiesTerm, out var capabilities),
            $"the community document must advertise the {capabilitiesTerm} extension (22.6.1)");
        Assert.Equal(JsonValueKind.Array, capabilities.ValueKind);
        var values = capabilities.EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(ActivityPubServerConstants.CapabilitySettings, values);
    }

    // --- Community document: iris:settings absent when no gate -----------------------------------

    [Fact]
    public async Task CommunityDoc_NoGate_DoesNotAdvertiseSettingsExtension()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/ap/v1/c/open");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // The iris:settings extension must be ABSENT (open has no manuallyApprovesMembers).
        var settingsTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + ActivityPubServerConstants.SettingsTerm;
        Assert.False(
            doc.RootElement.TryGetProperty(settingsTerm, out _),
            "the community document must NOT advertise the iris:settings extension when no gate is set");

        // The iris:capabilities list must NOT include "settings".
        var capabilitiesTerm = ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri + ActivityPubServerConstants.CapabilitiesTerm;
        Assert.True(
            doc.RootElement.TryGetProperty(capabilitiesTerm, out var capabilities),
            "the community document must advertise the iris:capabilities extension");
        var values = capabilities.EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.DoesNotContain(ActivityPubServerConstants.CapabilitySettings, values);
    }

    // --- Client-side: GetSettingsIri reads the extension from the document ----------------------

    [Fact]
    public async Task Client_GetSettingsIri__readsExtensionFromPersonDoc()
    {
        // Fetch carol's document as an Actor via the client (GetActorAsync).
        var carolIri = new Iri($"https://{Host}/ap/v1/u/{CarolHandle}");
        var actor = await FetchActorAsync(carolIri);
        Assert.NotNull(actor);

        var settingsIri = actor!.GetSettingsIri();
        Assert.NotNull(settingsIri);
        var settingsStr = settingsIri!.ToString();
        Assert.True(
            string.Equals(settingsStr, $"https://{Host}/ap/v1/u/{CarolHandle}/outbox", StringComparison.Ordinal),
            $"settings IRI mismatch: expected https://{Host}/ap/v1/u/{CarolHandle}/outbox, got {settingsStr}");
    }

    [Fact]
    public async Task Client_GetSettingsIri_returnsNullWhenNoGate()
    {
        var aliceIri = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        var actor = await FetchActorAsync(aliceIri);
        Assert.NotNull(actor);

        var settingsIri = actor!.GetSettingsIri();
        Assert.Null(settingsIri);
    }

    [Fact]
    public async Task Client_GetCapabilities_includesSettingsWhenGatePresent()
    {
        var carolIri = new Iri($"https://{Host}/ap/v1/u/{CarolHandle}");
        var actor = await FetchActorAsync(carolIri);
        Assert.NotNull(actor);

        var caps = actor!.GetCapabilities();
        Assert.Contains(ActivityPubServerConstants.CapabilitySettings, caps);
        Assert.Contains(ActivityPubServerConstants.CapabilityMute, caps);
        Assert.Contains(ActivityPubServerConstants.CapabilityRelay, caps);
    }

    [Fact]
    public async Task Client_GetCapabilities_excludesSettingsWhenNoGate()
    {
        var aliceIri = new Iri($"https://{Host}/ap/v1/u/{Handle}");
        var actor = await FetchActorAsync(aliceIri);
        Assert.NotNull(actor);

        var caps = actor!.GetCapabilities();
        Assert.DoesNotContain(ActivityPubServerConstants.CapabilitySettings, caps);
        Assert.Contains(ActivityPubServerConstants.CapabilityMute, caps);
        Assert.Contains(ActivityPubServerConstants.CapabilityRelay, caps);
    }

    // --- IriExtensions.SettingsOf helper ---------------------------------------------------------

    [Fact]
    public void SettingsOf_AppendsSettingsSegment()
    {
        var iri = new Iri("https://a.domain.local/ap/v1/c/devs");
        Assert.True(
            string.Equals(iri.SettingsOf().Value, "https://a.domain.local/ap/v1/c/devs/settings", StringComparison.Ordinal),
            $"SettingsOf mismatch: got {iri.SettingsOf().Value}");
    }

    [Fact]
    public void SettingsOf_OnPersonIri()
    {
        var iri = new Iri("https://a.domain.local/ap/v1/u/alice");
        Assert.True(
            string.Equals(iri.SettingsOf().Value, "https://a.domain.local/ap/v1/u/alice/settings", StringComparison.Ordinal),
            $"SettingsOf mismatch: got {iri.SettingsOf().Value}");
    }

    // --- Helpers --------------------------------------------------------------------------------

    private async Task<Actor?> FetchActorAsync(Iri actorIri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, actorIri.Value);
        using var response = await _client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var body = await response.Content.ReadAsStringAsync();
        return ActivityJson.Deserialize<Actor>(body);
    }
}
