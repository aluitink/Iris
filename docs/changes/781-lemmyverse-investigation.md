# 78.1 — Lemmyverse Investigation

Investigated Lemmy's ActivityPub wire format to determine what Iris needs to interoperate with Lemmy instances.

## Key Findings

### 1. Community Handles (`!community@domain`)

Lemmy uses the `!` prefix in its *UI display* for community references (e.g. `!rust@lemmy.ml`), but **WebFinger does NOT accept the `!` prefix**. The WebFinger `acct:` URI uses the bare community name:

```
# Works:
GET /.well-known/webfinger?resource=acct:rust@lemmy.ml
→ returns BOTH the Person (https://lemmy.ml/u/rust) AND the Group (https://lemmy.ml/c/rust)

# Fails:
GET /.well-known/webfinger?resource=acct:!rust@lemmy.ml
→ {"error": "unknown", "message": "Failed to resolve actor via webfinger"}
```

**Implication for Iris:** When a user types `!rust@lemmy.ml`, Iris must strip the `!` before doing WebFinger. The WebFinger response for `acct:rust@lemmy.ml` returns two `self` links — one `Person` and one `Group`. Iris should prefer the `Group` type for community contexts.

### 2. Group (Community) ActivityPub Document

A Lemmy community is an AS `Group` object:

```json
{
  "type": "Group",
  "id": "https://lemmy.ml/c/rust",
  "preferredUsername": "rust",
  "inbox": "https://lemmy.ml/c/rust/inbox",
  "followers": "https://lemmy.ml/c/rust/followers",
  "name": "Rust Programming",
  "summary": "<html>...</html>",
  "source": { "content": "markdown...", "mediaType": "text/markdown" },
  "icon": { "type": "Image", "url": "..." },
  "sensitive": false,
  "attributedTo": "https://lemmy.ml/c/rust/moderators",
  "postingRestrictedToMods": false,
  "outbox": "https://lemmy.ml/c/rust/outbox",
  "endpoints": { "sharedInbox": "https://lemmy.ml/inbox" },
  "featured": "https://lemmy.ml/c/rust/featured",
  "published": "...",
  "updated": "..."
}
```

**Key differences from Mastodon:**
- `attributedTo` points to a `/moderators` URL (not the group itself) — Iris should use `preferredUsername` + host for display, not `attributedTo`.
- `source` is a nested object `{content, mediaType}` (not a flat string like Mastodon's `summary` + `source`).
- `postingRestrictedToMods` is Lemmy-specific.
- `featured` is a Lemmy-specific collection of pinned posts.

### 3. Person (User) ActivityPub Document

A Lemmy user is an AS `Person` (not `Actor`):

```json
{
  "type": "Person",
  "id": "https://lemmy.ml/u/lemmy",
  "preferredUsername": "lemmy",
  "inbox": "https://lemmy.ml/u/lemmy/inbox",
  "outbox": "https://lemmy.ml/u/lemmy/outbox",
  "publicKey": { "id": "https://lemmy.ml/u/lemmy#main-key", ... },
  "endpoints": { "sharedInbox": "https://lemmy.ml/inbox" },
  "published": "..."
}
```

**Key differences from Mastodon:**
- No `name`, `summary`, or `icon` on the bare Person doc (these are in the HTML-embedded JSON, not the AP document).
- No `followers` URL on the Person (Mastodon has one).
- `@context` is `["https://join-lemmy.org/context.json", "https://www.w3.org/ns/activitystreams"]` (custom Lemmy context + AS 2.0).

### 4. Outbox: Posts and Comments

Community outbox items are `Announce` activities wrapping `Create` activities:

```json
{
  "type": "Announce",
  "actor": "https://lemmy.ml/c/rust",
  "object": {
    "type": "Create",
    "actor": "https://sh.itjust.works/u/steam_lover",
    "object": {
      "type": "Page",
      "id": "https://sh.itjust.works/post/66252370",
      "name": "Announcing Rust 1.98.1",
      "attributedTo": "https://sh.itjust.works/u/steam_lover",
      "to": ["https://lemmy.ml/c/rust", "https://www.w3.org/ns/activitystreams#Public"],
      "mediaType": "text/html",
      "attachment": [{ "href": "...", "mediaType": "text/html; charset=utf-8", "type": "Link" }],
      "image": { "type": "Image", "url": "..." },
      "sensitive": false,
      "published": "...",
      "audience": "https://lemmy.ml/c/rust",
      "tag": [{ "href": "...", "name": "#rust", "type": "Hashtag" }]
    }
  }
}
```

**Key observations:**
- Posts are `Page` type (not `Note`).
- `name` = post title; `content` = post body (HTML).
- `attachment` = linked URLs (Lemmy link posts).
- `audience` = the community the post belongs to.
- `tag` with `type: "Hashtag"` = community hashtag.
- Comments in the outbox would have `inReplyTo` pointing to the parent post/comment.
- **No vote/score data in AP documents** — scores (`score`, `upvotes`, `downvotes`) are only in Lemmy's private REST API (embedded in the HTML page as `window.isoData`).

### 5. Ranked Posts (Vote Scores)

Vote scores are **NOT in the ActivityPub wire format**. They exist only in Lemmy's private REST API (`/api/v3/...`), embedded in the HTML page's `window.isoData` JSON:

```json
"counts": {
  "post_id": 52317269,
  "comments": 0,
  "score": 8,
  "upvotes": 8,
  "downvotes": 0,
  "published": "2026-09-05T02:58:15.206984Z",
  "newest_comment_time": "..."
}
```

**Implication for Iris:** Iris cannot display Lemmy vote scores via AP alone. To show scores, Iris would need to either:
- (a) Call Lemmy's REST API (not standardized, version-sensitive), or
- (b) Not show scores (accept the limitation).

Recommendation: **(b)** for now — Iris is AP-native; scores are a Lemmy UI concern, not a federation concern.

### 6. Batched Streams

Lemmy outbox pagination uses standard AS2 `OrderedCollection` with `?page=N`:
- 50 items per page (typical).
- `totalItems` is present.
- `?page=1` returns the same as no page param (1-indexed).

This is compatible with Iris's existing `GetRepliesAsync` / outbox pagination.

### 7. `@context`

Lemmy uses a custom context: `["https://join-lemmy.org/context.json", "https://www.w3.org/ns/activitystreams"]`. The custom context defines Lemmy-specific terms (e.g. `lemmy:Community`, `lemmy:Post`). Iris does not need to resolve these for basic interoperability — the core AS2 types (`Person`, `Group`, `Page`, `Create`, `Announce`, `OrderedCollection`) are standard.

## Iris Interoperability Checklist

| Feature | Status | Action Needed |
|---------|--------|---------------|
| WebFinger for `!community@domain` | **FIXED (78.2)** | `!` stripped before WebFinger; `preferGroup` selects `Group` from dual-response |
| WebFinger for `user@domain` | Works | `acct:user@domain` → `Person` (no `!` needed) |
| Group doc rendering | **Works (78.3)** | `ActorProfile` already renders `preferredUsername`, `name`, `summary`, `icon`. `attributedTo` → `/moderators` is not read for display. |
| Person doc rendering | Limitation | No `name`/`summary`/`icon` on bare Person doc; fallback avatar + handle shown. Acceptable. |
| Page (post) rendering | **78.4 pending** | Handle `name` (title), `attachment` (links), `audience` (community), `tag` (hashtags) |
| Vote scores | Not available via AP | Accept limitation; no action |
| Outbox pagination | Compatible | Standard `OrderedCollection` + `?page=N` |
| `@context` | Compatible | Standard AS2 types used; Lemmy-specific terms ignored |

## 78.3 — Lemmy Group Doc Rendering (verification)

Verified that the existing `ActorProfile` + `ActorDetail` components handle Lemmy Group docs correctly:

- **`type: "Group"`** — `ActorDetail.IsCommunity` detects via `actor.Type?.FirstOrDefault() == "Group"`. The join-requests tab is shown when `manuallyApprovesMembers` is true (Lemmy-specific; defaults to false).
- **`preferredUsername`** — Rendered as the handle link in `ActorProfile`.
- **`name`** — Rendered as the display name (e.g., "Rust Programming"). `NameIsRedundant` correctly returns false when `name` ≠ `preferredUsername`.
- **`summary`** — Rendered as HTML (Lemmy uses `<p>` tags in `summary`).
- **`icon`** — Lemmy Group icons use `{"type":"Image","url":"..."}`. `ActorIdentityHelper.IconIri` already checks `IObject.Url` (the `url` property mapped to `IEnumerable<ILink>`).
- **`attributedTo`** → `/moderators` — Not read by `ActorProfile` for display. `CommunityDetail` reads it for membership checks, but the `/moderators` URL is not a valid actor IRI for `GetActorAsync`, so the membership check gracefully fails (no crash).
- **`outbox`/`followers`** — Standard collection IRIs; `ResolveCollectionIri` reads them correctly.

**No code changes needed.** The existing rendering is compatible with Lemmy Group docs.

**Known limitation:** Lemmy Person docs lack `name`, `summary`, and `icon` in the AP document. The `ActorProfile` component shows the handle + fallback avatar initial. This is acceptable — full user info requires Lemmy's private REST API.

## Next Steps (for implementation)

1. **78.2 — Lemmy community handle in Directory/Search — DONE:** Strip `!` from community handles before WebFinger; when WebFinger returns both `Person` and `Group`, prefer `Group` for `!`-prefixed queries.
2. **78.3 — Lemmy Group doc rendering — DONE (no code changes needed):** Verified existing `ActorProfile` + `ActorDetail` handle Lemmy Group docs correctly. See "78.3" section above.
3. **78.4 — Lemmy Page (post) rendering — DONE:** `ObjectView` now renders the `name` property as a title above the content for non-Actor objects. Lemmy Page posts have a `name` (title) + `content` (body); the title is now displayed. `attachment` (links) and `audience` (community) were already handled by existing code.

## 78.4 — Lemmy Page (Post) Rendering

Lemmy posts are AS `Page` objects with:
- `name` = post title (e.g., "Announcing Rust 1.98.1")
- `content` = post body (HTML)
- `attachment` = linked URLs (link posts)
- `audience` = the community the post belongs to
- `tag` = hashtags

**Changes made:**
- `ObjectView.razor.cs`: Added `Name` (reads `Obj?.Name?.FirstOrDefault()`) and `ActivityName` (reads `ActivityEmbeddedObject?.Name?.FirstOrDefault()`) properties.
- `ObjectView.razor`: Added title rendering (`<div class="object-title">`) before the content in both the `Create` branch (embedded object) and the direct object branch. Actor objects are excluded (their `name` is the display name, already rendered by `ActorProfile`).
- `app.css`: Added `.object-title` class (bold, 0.95rem, slight bottom margin).

**Already handled (no changes needed):**
- `attachment` — `GetRichAttachments()` / `MediaGallery` already reads the `attachment` property.
- `audience` — `GetAudienceIris()` / `ActivityAudienceIris` already reads the `audience` property.
- `tag` (hashtags) — `GetHashtagTags()` already reads the `tag` property.
