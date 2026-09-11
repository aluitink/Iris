# 75.3 — Proxy-fetch sync gap

## What was built

The AP proxy-fallback endpoint (`POST /ap/v1/proxy/{target}`) relayed remote responses
verbatim without persisting fetched objects or warming their media attachments. When a
proxied GET returned a successful ActivityPub JSON object (a Note, Article, etc.), the
handler now parses it, stores it in the `IObjectStore`, and warms its cross-origin media
attachments.

## Key changes

- `ProxyHandler` in `src/Iris.Server/ActivityPubServerExtensions.cs` gains two new
  parameters: `IPersistenceProvider persistence` and `IMediaWarmer mediaWarmer`.
- After reading the relayed response body, when the request was a GET, the status was
  2xx, and the content type is AP JSON (`application/activity+json` or
  `application/ld+json`), the handler:
  1. Deserializes the body via `ActivityJson.Deserialize<IObjectOrLink>(body)`.
  2. If the result is an `IObject` with a non-empty `Id`, stores it via
     `persistence.Objects.PutObjectAsync(obj, ct)`.
  3. If the instance base URI is configured, warms the object's attachments via
     `mediaWarmer.WarmAsync(obj, instanceBase, ct)`.
- The parse/store/warm is best-effort: a parse failure (the body is a collection, an
  activity, or malformed) or a store failure never breaks the relay.

## Design decision

Only GET responses are synced (not POST/PUT writes). A proxied write (a browser Create
POST to an outbox) is relayed with the body; the remote's 202 response is an activity
IRI or a bare status, not a content object to store. Only successful (2xx) responses are
synced — error responses (4xx/5xx) carry no object. Only AP JSON content types are
attempted — a proxy GET of a non-AP resource (e.g. a raw image) is relayed as-is.

## Test

- `Proxy_GetOfRemoteNote_StoresObjectInLocalStore` in
  `tests/Iris.Server.Tests/ProxyFallbackIntegrationTests.cs`: seeds a Note in B's
  store, proxy-GETs it from A, asserts the Note is now in A's store.

## Test counts

Full suite: 1666 passed, 0 failed, 17 skipped (was 1665 before this change).
