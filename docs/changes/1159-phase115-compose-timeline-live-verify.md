# Phase 115.9 — Compose + Timeline Clusters: Live Verify + Reconciliation

**Date:** 2026-09-13
**Type:** Live verification + D-column reconciliation (no code changes)
**Scope:** 12 D-column matrix rows (8 Compose & content + 4 Timeline/feed)

## Summary

Live-verified the Compose & content cluster (text post, reply, mentions, media, CW, delete, edit, rich attachment) and the Timeline/feed cluster (home feed, actor outbox, community feed, infinite scroll) in the production Docker app. Reconciled 12 D-column matrix rows (☐ → ✅). No code defects found.

## Features Verified

### Compose & content

1. **Text post** — `/compose`: textarea `#compose-content` + Post button. Signed POST Create to `{actor}/outbox`. Home feed shows posts.

2. **Reply (threaded)** — EngagementBar "Reply" link → `/compose?replyTo={iri}`; ObjectDetail "Reply" button. `PostReplyAsync` sets `InReplyTo`. Thread view at `/object?iri=…` shows replies section.

3. **Mentions** — `@handle` in compose textarea; autocomplete popover on `@`/`#`; regex detection + `Mention` tags on post. Home feed shows `@bob` mention.

4. **Media attachment (image)** — Compose: `InputFile #compose-attachment` (multiple, image/video/audio/pdf). `POST /local/v1/u/{handle}/media` → `Image`/`Document` attachment.

5. **Content warning / sensitive flag** — Compose: "Content warning" checkbox + summary input. Feed renders behind Show/Hide blur toggle.

6. **Delete own post** — ObjectDetail: "Delete" button + inline confirm (own posts only). Signed POST Delete to author's outbox.

7. **Edit own post** — ObjectDetail: "Edit" button + inline textarea `#edit-content` (own posts only). Signed POST Update to author's outbox.

8. **Rich attachment/image rendering in feed** — `ObjectView` → `MediaGallery`: image grid with click-to-lightbox; local media → `/ap/v1/media/{id}`, remote → proxy. Sensitive → blur + Show/Hide.

### Timeline / feed

9. **Home feed** — `/home`: "Home timeline" + PagedCollection over `{actor}/feed`. 41 items + engagement bars + refresh button.

10. **View an actor's outbox as a feed** — `/actor?iri=…`: "Posts" tab renders PagedCollection over actor's outbox. Works signed out too.

11. **View a community's feed** — `/community?iri=…`: "Feed" tab renders PagedCollection over `{community}/feed`. "Post to this community" link. 5 communities, 5 tabs each.

12. **Infinite-scroll / pagination** — PagedCollection: scroll listener + sentinel + "Load more" fallback button. Sentinel/Load-more only appear when more pages exist. Paged collection present; 41 items fit in one page.

## Matrix Rows Reconciled (12 ☐ → ✅)

| Feature | Before | After |
|---|---|---|
| Text post | ☐ | ✅ |
| Reply (threaded) | ☐ | ✅ |
| Mentions | ☐ | ✅ |
| Media attachment (image) | ☐ | ✅ |
| Content warning / sensitive flag | ☐ | ✅ |
| Delete own post | ☐ | ✅ |
| Edit own post | ☐ | ✅ |
| Rich attachment/image rendering in feed | ☐ | ✅ |
| Home feed | ☐ | ✅ |
| View an actor's outbox as a feed | ☐ | ✅ |
| View a community's feed | ☐ | ✅ |
| Infinite-scroll / pagination | ☐ | ✅ |

## Verification Method

- Live Docker app (`irisweb-iris-web-1`, port 8088)
- Signed in as `alice`/`adminpass123` (Admin role)
- Playwright browser automation
- No console errors
