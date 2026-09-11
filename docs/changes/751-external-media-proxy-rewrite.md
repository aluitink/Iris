# 75.1 — External media proxy rewrite (client render boundary fix)

## Summary

Fixed the production Web client's media render boundary so cross-origin external media loads correctly. Previously, `RewriteMediaToSameOrigin` stripped the host from cross-origin URLs, producing broken relative paths that 404'd. Now cross-origin media routes through the media proxy.

## The bug

Three copies of `RewriteMediaToSameOrigin` (in `ObjectView.razor.cs`, `NotificationRow.razor`, `ActorIdentityHelper.cs`) all had the same logic:

```csharp
// OLD: strips host from ANY absolute HTTPS URL
return uri.AbsolutePath + uri.Query;
```

For local media (`https://iris.luit.ink/ap/v1/media/{id}`), this correctly produces `/ap/v1/media/{id}`. But for cross-origin external media (`https://remote.example/img.png`), it produced `/img.png` — a broken relative path that 404'd.

## The fix

```csharp
// NEW: local media → relative path; cross-origin → media proxy
if (uri.AbsolutePath.StartsWith("/ap/v1/media/", StringComparison.Ordinal))
{
    return uri.AbsolutePath + uri.Query;  // local: strip to relative
}
return $"/ap/v1/media/proxy?url={Uri.EscapeDataString(mediaIri)}";  // external: proxy
```

The media proxy endpoint (`GET /ap/v1/media/proxy?url=...`) already existed and worked correctly — it fetches the external URL, stores it in the media store (deduped by content hash), and serves it same-origin with `Cache-Control: max-age=31536000, immutable`. The fix just makes the client actually route cross-origin URLs through it.

## Files changed

- `apps/Iris.Web.Client/Components/ObjectView.razor.cs` — `RewriteMediaToSameOrigin`
- `apps/Iris.Web.Client/Components/NotificationRow.razor` — `RewriteMediaToSameOrigin`
- `apps/Iris.Web.Client/Components/ActorIdentityHelper.cs` — `RewriteMediaToSameOrigin`

## Verification

Live-verified via Playwright on the home timeline:
- 4 local media images: load correctly (`/ap/v1/media/{id}`, naturalWidth 768/761)
- 1 external image (mastodon.world turkey from wasabi S3): loads correctly through the proxy (`/ap/v1/media/proxy?url=...`, naturalWidth 761, complete)
- Proxy endpoint directly: `200 image/png`, 1.8MB
- Zero console errors

Build: 0 warnings, 0 errors. Tests: 1665 passed, 0 failed, 17 skipped (one flaky Server test passes in isolation — pre-existing).

## Remaining Phase 75 gaps (for future slices)

1. **Warmer media-type gap**: `DefaultMediaWarmer.WarmAsync` only warms `Image` attachments (via `GetMediaAttachments()`). `Document`, `Audio`, `Video` attachments are not pre-downloaded. Fix: use `GetRichAttachments()` or extend the warmer.

2. **Proxy-fetch sync gap**: `POST /ap/v1/proxy/{target}` (the AP proxy-fallback for browsing remote outboxes) relays the remote response verbatim without storing the object or warming its attachments. External objects reached via proxy-fetch rely entirely on the reactive media proxy at render time.

3. **Update handler gap**: `UpdateActivityHandler` stores updated objects but doesn't call the media warmer, so an `Update` that adds new attachments won't re-warm them.
