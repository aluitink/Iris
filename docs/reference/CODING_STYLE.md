# Iris — Coding Style Guide

> Part of the [Iris plan](../../PLAN.md). See also [Architecture](ARCHITECTURE.md), [Projects](PROJECTS.md), [Testing](TESTING.md), [Roadmap](../ROADMAP.md).
>
> This guide is **binding** for all code in the solution. Where it conflicts with a habit, the guide wins. The section on [3rd-Party ActivityStreams Types](#3rd-party-activitystreams-types) is the most important — it governs how we interoperate with `KristofferStrube.ActivityStreams`, and consistency there is what keeps the codebase legible.

## General C# Conventions

- **TFM: net10.0** for all projects (client, server, tests, samples).
- **C# latest**, `Nullable` enabled, `TreatWarningsAsErrors` on.
- **File-scoped namespaces** everywhere.
- **`System.Text.Json` exclusively** — no Newtonsoft, no `JavaScriptSerializer`.
- **One public type per file**; file name matches the type name.
- **XML doc comments** on all public API (types, members, enums). Internal/private members: comment only when non-obvious.
- **`CancellationToken`** is the **last** parameter on every `async` method, named `ct`, with a default of `default` only on public convenience overloads.
- **No `async void`** anywhere (event handlers excepted, and even then prefer `async Task` + fire-and-forget with exception capture).
- **No `HttpClient` ownership assumptions** in library code — accept `IHttpClientFactory` or a pre-built `HttpClient`.
- **Prefer `record` for immutable value types** (e.g. `HttpRequestMetadata`, `CacheEntry<T>`, `Iri`); prefer `class` for mutable domain/DTO types.
- **Collection expressions** (`[1, 2, 3]`) over `new List<T> { ... }` where the target type is `IEnumerable<T>`/`IReadOnlyList<T>`.
- **No magic strings** for IRIs, content types, or header names — use the constants in `ActivityJson` / `Iri` / dedicated `*Constants` classes.
- **Central package management** (`Directory.Packages.props`) — no version numbers in `.csproj` files.

## Naming Conventions

| Thing | Convention | Example |
|---|---|---|
| Types | PascalCase | `KeyPair`, `SigningHandler` |
| Interfaces | `I` prefix | `IActivityPubClient`, `ICache<T>` |
| Methods / Properties | PascalCase | `GetActorAsync`, `KeyId` |
| Local variables / parameters | camelCase | `actorId`, `ct` |
| Private fields | `_camelCase` | `_keyStore` |
| Constants | PascalCase (C# standard) | `Public`, `ContentType` |
| Async methods | `...Async` suffix | `SendActivityAsync` |
| Boolean properties/params | `Is`/`Has`/`Can`/`Should` prefix or affirmative verb | `IsLastPage`, `BypassCache` |
| Extension methods | Verb, on the type they extend | `IriExtensions.InboxOf(this Iri)` |
| Events | Past tense | `ActivityReceived` |

## Project & Dependency Rules

- `Iris.Core` depends on `KristofferStrube.ActivityStreams` + BCL **only**. No HTTP, no DI, no persistence.
- `Iris.Client` depends on `Iris.Core` + BCL.
- `Iris.Server` depends on `Iris.Core` + `Iris.Client` + ASP.NET Core.
- `Iris.Server.InMemory` depends on `Iris.Server`.
- **No upward dependencies.** `Iris.Core` never references `Iris.Client` or `Iris.Server`.
- **No new NuGet packages** without a note in the [Roadmap](../ROADMAP.md) and a justification. The ActivityStreams package is the only non-BCL dependency in `Iris.Core`.

## Error Handling

- **Don't catch what you can't handle.** Library code throws specific exceptions; host apps decide policy.
- **No empty `catch` blocks.** No `catch { }`, no `catch (Exception) { /* ignore */ }`.
- **Validate arguments** in public API with `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIf...` (guard clauses at the top).
- **Don't throw for expected conditions** (e.g. "actor not found" → return `null`/`ValueTask<bool>`, don't throw).
- **Log, don't throw**, for recoverable delivery failures (dead-letter, retry).

## Async Conventions

- Every public API that does I/O is `async Task<T>` / `async ValueTask<T>`.
- **No `.Result` / `.Wait()`** in library code.
- **`IAsyncEnumerable<T>`** for paged/streaming APIs (collections, feeds) — see [Rich Paged Collections](#rich-paged-collections).
- **Background work** (stale-while-revalidate refresh, delivery queue) uses `Channel<T>` + `IHostedService`, never `Task.Run` fire-and-forget.

## 3rd-Party ActivityStreams Types

This is the heart of the guide. `KristofferStrube.ActivityStreams` (v0.2.4) provides **all** ActivityStreams/ActivityPub object, actor, activity, link, and collection types, plus the `System.Text.Json` converters. **We do not re-implement any of them.** The rules below keep our usage consistent.

### The Library's Shape (what we're working with)

- **Namespace**: `KristofferStrube.ActivityStreams` (all public types).
- **All types are plain `public class`** (not records) with a parameterless constructor that sets `Type`.
- **All properties are nullable** (`?`) with `{ get; set; }`.
- **Multi-valued properties are `IEnumerable<T>?`** — a single JSON value deserializes into a 1-element enumerable and serializes back as a **scalar** (not an array), via the library's `OneOrMultipleConverter<T>`.
- **`Id` is `string?`**, **`Link.Href` is `Uri?`** — the library has no IRI wrapper type.
- **`Type` is auto-set** by each concrete type's constructor (e.g. `new Person()` → `Type = ["Person"]`).
- **`@context` is pre-populated** to the ActivityStreams context URI by default.
- **Unknown/extra JSON properties** land in `[JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData` on `Object` and `Endpoints`.
- **Polymorphic deserialization** is driven by the `"type"` property via the library's converters.
- **`IntransitiveActivity` forbids `Object`** at compile time (`[Obsolete(..., error)]`) — use `Target`/`Origin` instead.

### Rule 1 — Deserialize into the range interface, then cast

Never deserialize a polymorphic payload directly into a concrete type. Deserialize into `IObjectOrLink` (or `IObject` / `ILink`), then cast.

```csharp
// ✅ Correct
IObjectOrLink payload = ActivityJson.Deserialize<IObjectOrLink>(json);
if (payload is Like like) { /* ... */ }

// ❌ Wrong — bypasses the polymorphic converter; unknown types won't resolve
Like like = ActivityJson.Deserialize<Like>(json);
```

Use the library's `As<T>()` helper (or an `is` pattern) for the cast. **Always null-check / pattern-match after deserializing** — every property is nullable.

### Rule 2 — Construct with object initializers; let the constructor set `Type`

```csharp
// ✅ Correct — constructor sets Type = ["Note"]
Note note = new()
{
    Id = "http://example.org/note/1",
    Name = ["A Note"],
    Content = ["<p>Hello</p>"],
    To = [new Link { Href = new Uri("https://www.w3.org/ns/activitystreams#Public") }],
};

Follow follow = new()
{
    Id = "http://example.org/follow/1",
    Actor = [new Person { Id = "http://sally.example.org" }],
    Object = [new Link { Href = new Uri("http://john.example.org") }],
};
```

- **Do not set `Type` manually** unless you're expressing a custom/extended type (e.g. `Type = ["Activity", "https://iris.example/ns#Check"]`).
- **Do not set `JsonLDContext`** unless targeting a different vocabulary — it's pre-populated.

### Rule 3 — Multi-valued properties: use collection expressions, expect `IEnumerable<T>?`

```csharp
// ✅ Single element — serializes as a JSON scalar, not an array
note.To = [new Link { Href = publicIri }];

// ✅ Multiple elements — serializes as a JSON array
note.Audience = [linkA, linkB];

// ❌ Don't use arrays or List<T> for these properties
note.To = new ILink[] { link };
```

When **reading** a multi-valued property, treat it as possibly-null and possibly-empty:

```csharp
string? firstActorId = activity.Actor?.FirstOrDefault()?.Id;
```

### Rule 4 — `Id` is `string?`; wrap in `Iri` at the Iris boundary

The library uses `string?` for `Id`. Iris uses the `Iri` value type for identity. **Convert at the boundary** — don't leak raw `string` IRIs through Iris APIs, and don't force `Iri` into the library's types.

```csharp
// ✅ Boundary conversion
Iri actorIri = new(actor.Id!);          // library → Iris
actor.Id = iri.ToString();              // Iris → library

// ❌ Don't do this in Iris public API signatures
Task<string> GetActorIdAsync(...);      // leak raw string
```

`Iri` ↔ `string` conversion helpers live in `IriExtensions`. For `Link.Href` (`Uri?`), use `Iri`'s `Uri`-based constructor.

### Rule 5 — Serialize with `ActivityJson`, never raw `JsonSerializer` defaults

All serialization/deserialization of ActivityStreams types goes through the `ActivityJson` static class in `Iris.Core`, which holds the pre-configured `JsonSerializerOptions` (with the library's converters registered and `@context` injection). This guarantees consistent `@context`, content-type, and converter behavior.

```csharp
// ✅ Correct
string json = ActivityJson.Serialize(activity);
IObjectOrLink obj = ActivityJson.Deserialize<IObjectOrLink>(json);

// ❌ Wrong — bypasses Iris's configured options
string json = JsonSerializer.Serialize(activity);
```

`ActivityJson` also exposes the content-type constants (`application/activity+json`, `application/ld+json`).

### Rule 6 — Non-standard fields go in `ExtensionData`, not new properties

When we need to carry a field the library doesn't model (e.g. our `iris:capabilities`, or a `privateKey` on an authenticated actor doc), we use the library's `[JsonExtensionData]` mechanism — **we do not subclass or re-declare the library's types**.

```csharp
// ✅ Adding iris:capabilities to a community document
community.ExtensionData ??= new Dictionary<string, JsonElement>();
community.ExtensionData["iris:capabilities"] = JsonSerializer.SerializeToElement(
    new[] { "feed", "members", "search" });

// ✅ Reading a non-standard field
if (actor.ExtensionData is { } ext && ext.TryGetValue("privateKey", out var pk))
{
    string pem = pk.GetString()!;
}
```

- **Never** create `class MyPerson : Person` to add a property. Use `ExtensionData`.
- **Never** create a parallel "shadow" type that mirrors a library type. Compose or extend via `ExtensionData`.
- Iris-specific **wrapper** types (e.g. the client's `CollectionPage` wrapping `OrderedCollectionPage`) are allowed — they *contain* a library type, they don't *re-declare* it.

### Rule 7 — `IntransitiveActivity`: use `Target`/`Origin`, never `Object`

`IntransitiveActivity` (and its subtypes `Arrive`, `Travel`, `Question`) compile-error if you set `Object`. Use `Target` / `Origin` / `Instrument` instead.

```csharp
// ✅
var arrive = new Arrive { Target = [obj], Actor = [actor] };

// ❌ Compile error
var arrive = new Arrive { Object = [obj] };
```

### Rule 8 — Type checks: pattern-match on the concrete type

```csharp
// ✅
switch (activity)
{
    case Follow follow: /* ... */ break;
    case Like like: /* ... */ break;
    default: /* unknown activity type */ break;
}

// ✅ For "is this an actor of any kind"
if (obj is Actor actor) { /* ... */ }
```

Don't compare `Type` strings manually — the converters already resolved the concrete type.

### Rule 9 — Don't fight nullability

Every library property is nullable. After deserializing, **null-check before use**. Before serializing, you don't need to null-check (unset properties are omitted via `JsonIgnoreCondition.WhenWritingDefault`).

```csharp
// ✅ After deserialize
string? username = actor.PreferredUsername;   // may be null
uint? total = page.TotalItems;                 // may be null

// ✅ Before serialize — just set what you have
var note = new Note { Id = id, Content = content };   // Name, Summary, etc. omitted
```

### Rule 10 — `@context` and vocabulary

- Leave `JsonLDContext` at its default (ActivityStreams context) for all standard federation traffic.
- When emitting Iris-namespaced terms (`iris:capabilities`, etc.), the term is a **full IRI** in the configurable namespace — no `@context` change needed.
- If a future feature requires a different `@context`, set it explicitly on the object and document why.

### Quick Reference — Common Library Types

| Type | Base | Key properties | Notes |
|---|---|---|---|
| `Object` | `ObjectOrLink` | `Id`, `Type`, `Name`, `Content`, `To`, `Cc`, `Bto`, `Bcc`, `Tag`, `Url`, `Attachment`, `AttributedTo`, `Audience`, `InReplyTo`, `Replies`, `Published`, `Updated`, `ExtensionData` | Base for all objects |
| `Actor` | `Object` | `Outbox`, `Inbox`, `Followers`, `Following`, `Liked`, `PreferredUsername`, `Endpoints` | Base for actors |
| `Person` / `Application` / `Service` / `Group` / `Organization` | `Actor` | — | Concrete actor types; `Group` = community |
| `Activity` | `Object` | `Actor`, `Object`, `Target`, `Result`, `Origin`, `Instrument` | Base for activities |
| `IntransitiveActivity` | `Activity` | (no `Object`) | Use `Target`/`Origin` |
| `Follow` / `Accept` / `Reject` / `Create` / `Like` / `Announce` / `Undo` / `Update` / `Add` / `Remove` / `Block` / `Flag` / `Invite` / `Offer` / `Join` / `Leave` / `Move` / `Read` / `Listen` / `View` / `Dislike` / `Ignore` / `Delete` | `Activity` | — | Concrete activities |
| `Note` / `Article` / `Event` / `Place` / `Profile` / `Relationship` / `Tombstone` | `Object` | — | Concrete objects |
| `Document` → `Audio` / `Image` / `Video` / `Page` | `Object` | — | Media objects |
| `Collection` | `Object` | `Items`, `OrderedItems`, `TotalItems`, `Current`, `First`, `Last` | Base for collections |
| `OrderedCollection` | `Collection` | — | Ordered |
| `CollectionPage` | `Collection` | `PartOf`, `Next`, `Prev` | Page |
| `OrderedCollectionPage` | `CollectionPage` | `StartIndex` | Ordered page — our pagination wire format |
| `Link` | `ObjectOrLink` | `Href` (`Uri?`), `Rel`, `Hreflang`, `Height`, `Width` | Link |
| `Mention` | `Link` | — | Mention |
| `Endpoints` | — | `ProxyUrl`, `SharedInbox`, `ProvideClientKey`, `SignClientKey`, `OauthAuthorizationEndpoint`, `OauthTokenEndpoint`, `ExtensionData` | Actor endpoints |
| `IObjectOrLink` / `IObject` / `ILink` / `IImageOrLink` / `ICollectionOrLink` / `ICollectionPageOrLink` / `IEndpointsOrLink` | interfaces | — | **Deserialize into these** |

## Rich Paged Collections

Iris's client exposes collections as `IAsyncEnumerable<CollectionPage>`. The `CollectionPage` is an **Iris wrapper** (Rule 6) that *contains* an `OrderedCollectionPage`:

```csharp
public sealed class CollectionPage
{
    public OrderedCollectionPage Page { get; init; }          // the library type
    public IReadOnlyList<IObjectOrLink> Items { get; init; }  // flattened items
    public Iri? NextPage { get; init; }
    public Iri? PrevPage { get; init; }
    public int? TotalItems { get; init; }
    public bool IsLastPage => NextPage is null;
}
```

- The wrapper **contains** the library type; it does not re-declare it.
- `Items` is `IReadOnlyList<IObjectOrLink>` — callers pattern-match each item (Rule 8).
- `NextPage`/`PrevPage` are `Iri?` (Rule 4) — converted from the library's `Link.Href` (`Uri?`) at the boundary.

## Testing Conventions

- **xUnit** for all tests.
- **Integration-first** — see [Testing](TESTING.md). Unit tests only for pure logic (crypto, IRI, cache).
- **Test naming**: `MethodName_Scenario_ExpectedOutcome` (e.g. `SignAndVerify_RoundTrip_BothProfiles_Succeeds`).
- **One `Fact`/`Theory` per behavior**; no multi-assert "kitchen sink" tests.
 - **Fixtures** live in `Iris.Testing` (shared harness) — test projects reference it, don't duplicate setup. The single real-pipeline `TestServer` bootstrap is **`ActivityPubHostFactory.Create(ActivityPubHostOptions)`**; do not add a private per-test `StartServer`. Seeding uses **`TestSeeder`**; wire-format assertions use **`JsonDoc`** + **`Jwk`**.
- **No mocking of ActivityStreams types** — they're plain data; construct real instances.
- **Assert on the wire format** (serialized JSON) for serialization tests, not just in-memory state.

## Documentation

- **XML doc** on all public API. First sentence is a summary (ends with a period); subsequent sentences are remarks.
- **`<remarks>`** for non-obvious behavior (e.g. "This method bypasses the cache when `bypassCache` is true").
- **`<param>` / `<returns>` / `<exception>`** on public methods.
- **Code samples** in XML doc (`<code>`) for high-level APIs (`IActivityPubClient`, etc.).
- **README.md** per project: one-paragraph purpose, install, minimal usage example.
- **This guide** lives in `docs/reference/CODING_STYLE.md` and is linked from `PLAN.md`.

## Lint & Formatting

- **`.editorconfig`** at the repo root enforces: 4-space indent, allman braces, `var` for obvious types, no `this.` prefix, trailing comma in collection expressions.
- **`dotnet format`** is the canonical formatter; run it before committing.
- **Analyzers**: `TreatWarningsAsErrors` on; enable `CA` (code analysis) and `IDE` (IDE analyzer) rule sets at `Warning` severity, promoted to `Error` for the rules we care about.
- **No `#pragma warning disable`** without a `// TODO:` comment explaining why and linking an issue.

## Definition of Done (per change)

A change is complete when:

1. It builds with `TreatWarningsAsErrors` on.
2. `dotnet format` is clean.
3. It follows this guide (especially the [3rd-Party ActivityStreams Types](#3rd-party-activitystreams-types) rules).
4. It has the integration tests that prove its end-to-end behavior (per [Testing](TESTING.md)).
5. Public API changes are reflected in XML doc and, if user-facing, in the project README.
