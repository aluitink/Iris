using System.Collections.Concurrent;
using System.Text.Json;
using Iris.Core;
using Iris.Server.Persistance;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="ICommunityStore"/> (Phase 16.4, production persistence): <see cref="Group"/>
/// (community) documents, memberships, and follow/follower edges persisted to a single JSON file that
/// survives a restart.
/// </summary>
/// <remarks>
/// The file holds four sections: <c>communities</c> (a map of community IRI →
/// <see cref="FilePersistence.DocumentEntry"/>), <c>members</c>, <c>follows</c>, and <c>followers</c>
/// (each a map of community IRI → list of member/followed/follower IRIs). Communities round-trip
/// through <see cref="ActivityJson"/> so the concrete type is preserved. Thread-safe (the underlying
/// <see cref="FilePersistence"/> serializes reads/writes).
/// </remarks>
public sealed class FileBackedCommunityStore : ICommunityStore, IDisposable
{
    private const string Communities = "communities";
    private const string Members = "members";
    private const string Follows = "follows";
    private const string Followers = "followers";

    private readonly FilePersistence _file;

    /// <summary>
    /// Initializes a new file-backed community store over <paramref name="path"/> (creating the file on
    /// the first write; the directory must already exist).
    /// </summary>
    /// <param name="path">The path of the store file.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null or empty.</exception>
    public FileBackedCommunityStore(string path)
        : this(new FilePersistence(path, CommunityToDocument, CommunityFromDocument))
    {
    }

    /// <summary>
    /// Initializes a new store over an existing <see cref="FilePersistence"/> (used by tests).
    /// </summary>
    /// <param name="file">The backing file store. Must not be null.</param>
    public FileBackedCommunityStore(FilePersistence file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <inheritdoc/>
    public Task<bool> TryGetCommunityAsync(Iri communityIri, out Group? community, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (found, c) = _file.Snapshot(s =>
        {
            if (CommunityMap(s).TryGetValue(communityIri.Value, out var entry) && entry is not null)
            {
                return (true, ActivityJson.Deserialize<IObjectOrLink>(entry.Json) as Group);
            }

            return (false, (Group?)null);
        });

        community = c;
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutCommunityAsync(Group community, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(community);
        if (string.IsNullOrWhiteSpace(community.Id))
        {
            throw new ArgumentException("Community must have a non-null Id.", nameof(community));
        }

        var iri = new Iri(community.Id);
        var json = ActivityJson.Serialize(community);
        return _file.WithStateAsync(s =>
        {
            CommunityMap(s)[iri.Value] = new FilePersistence.DocumentEntry(iri, json);
            return 0;
        }, true, ct);
    }

    /// <inheritdoc/>
    public Task<bool> AddMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => AddUnique(SetMap(s, Members), communityIri.Value, actorIri.Value), true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => RemoveValue(SetMap(s, Members), communityIri.Value, actorIri.Value), true, ct);

    /// <inheritdoc/>
    public Task<bool> IsMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => Contains(SetMap(s, Members), communityIri.Value, actorIri.Value), ct);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetMembersAsync(Iri communityIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => ToIris(SetMap(s, Members), communityIri.Value), ct);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetFollowsAsync(Iri communityIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => ToIris(SetMap(s, Follows), communityIri.Value), ct);

    /// <inheritdoc/>
    public Task<bool> AddFollowAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => AddUnique(SetMap(s, Follows), communityIri.Value, actorIri.Value), true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFollowAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => RemoveValue(SetMap(s, Follows), communityIri.Value, actorIri.Value), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetFollowersAsync(Iri communityIri, CancellationToken ct = default)
        => _file.SnapshotAsync(s => ToIris(SetMap(s, Followers), communityIri.Value), ct);

    /// <inheritdoc/>
    public Task<bool> AddFollowerAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => AddUnique(SetMap(s, Followers), communityIri.Value, actorIri.Value), true, ct);

    /// <inheritdoc/>
    public Task<bool> RemoveFollowerAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default)
        => _file.WithStateAsync(s => RemoveValue(SetMap(s, Followers), communityIri.Value, actorIri.Value), true, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<Iri>> GetAllCommunityIrisAsync(CancellationToken ct = default)
        => _file.SnapshotAsync<IReadOnlyCollection<Iri>>(s =>
        {
            var result = new List<Iri>();
            foreach (var key in CommunityMap(s).Keys)
            {
                result.Add(new Iri(key));
            }

            return result;
        }, ct);

    /// <summary>
    /// The community document map for the current state (community IRI value → entry), created on demand.
    /// </summary>
    private static ConcurrentDictionary<string, FilePersistence.DocumentEntry> CommunityMap(ConcurrentDictionary<string, object> state)
        => (ConcurrentDictionary<string, FilePersistence.DocumentEntry>)(state.TryGetValue(Communities, out var d) ? d! : state[Communities] = new ConcurrentDictionary<string, FilePersistence.DocumentEntry>());

    /// <summary>
    /// The named set map for the current state (community IRI value → list of IRI values), created on
    /// demand.
    /// </summary>
    private static ConcurrentDictionary<string, List<string>> SetMap(ConcurrentDictionary<string, object> state, string name)
        => (ConcurrentDictionary<string, List<string>>)(state.TryGetValue(name, out var d) ? d! : state[name] = new ConcurrentDictionary<string, List<string>>());

    /// <summary>
    /// Adds a value to a set, returning true when newly added (idempotent).
    /// </summary>
    private static bool AddUnique(ConcurrentDictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<string>();
            map[key] = list;
        }

        lock (list)
        {
            if (list.Contains(value))
            {
                return false;
            }

            list.Add(value);
            return true;
        }
    }

    /// <summary>
    /// Removes a value from a set, returning true when removed.
    /// </summary>
    private static bool RemoveValue(ConcurrentDictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            return false;
        }

        lock (list)
        {
            return list.Remove(value);
        }
    }

    /// <summary>
    /// Returns whether a value is in a set.
    /// </summary>
    private static bool Contains(ConcurrentDictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            return false;
        }

        lock (list)
        {
            return list.Contains(value);
        }
    }

    /// <summary>
    /// Returns the set's values as IRI list (empty when absent).
    /// </summary>
    private static IReadOnlyCollection<Iri> ToIris(ConcurrentDictionary<string, List<string>> map, string key)
    {
        var result = new List<Iri>();
        if (map.TryGetValue(key, out var list))
        {
            lock (list)
            {
                foreach (var v in list)
                {
                    result.Add(new Iri(v));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Serializes all four sections to a JSON document.
    /// </summary>
    private static JsonDocument CommunityToDocument(ConcurrentDictionary<string, object> state)
    {
        var communities = state.TryGetValue(Communities, out var c)
            ? (ConcurrentDictionary<string, FilePersistence.DocumentEntry>)c!
            : new ConcurrentDictionary<string, FilePersistence.DocumentEntry>();
        var members = SetMap(state, Members);
        var follows = SetMap(state, Follows);
        var followers = SetMap(state, Followers);

        var communitiesJson = JsonSerializer.Serialize(communities.ToDictionary(kv => kv.Key, kv => kv.Value), FilePersistence.JsonOptions);
        var membersJson = JsonSerializer.Serialize(members.ToDictionary(kv => kv.Key, kv => kv.Value), FilePersistence.JsonOptions);
        var followsJson = JsonSerializer.Serialize(follows.ToDictionary(kv => kv.Key, kv => kv.Value), FilePersistence.JsonOptions);
        var followersJson = JsonSerializer.Serialize(followers.ToDictionary(kv => kv.Key, kv => kv.Value), FilePersistence.JsonOptions);
        return JsonDocument.Parse($"{{\"{Communities}\":{communitiesJson},\"{Members}\":{membersJson},\"{Follows}\":{followsJson},\"{Followers}\":{followersJson}}}");
    }

    /// <summary>
    /// Populates all four sections from the file's root element.
    /// </summary>
    private static void CommunityFromDocument(JsonElement root, ConcurrentDictionary<string, object> state)
    {
        var communities = new ConcurrentDictionary<string, FilePersistence.DocumentEntry>();
        if (root.TryGetProperty(Communities, out var cEl) && cEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in cEl.EnumerateObject())
            {
                var entry = prop.Value.Deserialize<FilePersistence.DocumentEntry>(FilePersistence.JsonOptions);
                if (entry is not null)
                {
                    communities[entry.Iri.Value] = entry;
                }
            }
        }

        state[Communities] = communities;

        foreach (var name in new[] { Members, Follows, Followers })
        {
            var setMap = new ConcurrentDictionary<string, List<string>>();
            if (root.TryGetProperty(name, out var sEl) && sEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in sEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<string>();
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            var s = item.GetString();
                            if (s is not null)
                            {
                                list.Add(s);
                            }
                        }

                        setMap[prop.Name] = list;
                    }
                }
            }

            state[name] = setMap;
        }
    }

    /// <summary>
    /// Releases the store's file lock. The file on disk is left in place (the data is durable);
    /// this only frees the <see cref="FilePersistence"/> lock that serializes reads/writes.
    /// </summary>
    public void Dispose() => _file.Dispose();
}