# 138.25 follow-up — UI surfacing of Lemmy metadata terms

## What changed

Surfaced the five `iris:` extension terms (rendered server-side by 138.25) in the Blazor WASM UI.
Client-only change; no server code touched.

### `iris:locked` — disable the reply composer

- **`EngagementBar.razor`**: new `[Parameter] bool Locked`. When true, the reply link is replaced
  with a disabled `<span>` carrying a lock icon and the tooltip
  "Replies are disabled (post is locked)".
- **`ObjectView.razor`**: both `EngagementBar` call-sites (Create branch and bare-object branch)
  now pass `Locked="IsLocked"`.
- **`ObjectView.razor.cs`**: new `IsLocked` property reads
  `Obj?.GetLocked(Session.IrisNamespaceBase?.Value)`.
- **`ObjectDetail.razor`**: new `IsLocked` property (reads off `SubjectObject`). When true the
  "Reply" button is replaced by an inline lock indicator
  ("Replies disabled (post is locked)").

### `iris:featured` — pinned indicator

- **`ObjectView.razor.cs`**: new `IsFeatured` property reads
  `Obj?.GetFeatured(Session.IrisNamespaceBase?.Value)`.
- **`ObjectView.razor`**: a `📌 Pinned` badge (`.object-featured-badge`) renders below the title
  in both the Create branch and the bare-object branch when `IsFeatured` is true.

### `iris:language` — prefer the `iris:` term over bare `inLanguage`

- **`ObjectView.razor.cs`**: new `ObjectLanguage` property that prefers
  `Obj?.GetLanguage(ns)` (the `iris:language` term) and falls back to
  `Obj?.GetInLanguage()` (the standard AS `inLanguage` extension).
- **`ObjectView.razor`**: both language render sites (Create + bare-object branches) now use
  `ObjectLanguage` instead of `ArticleInLanguage`.

### `iris:communityNsfw` — NSFW banner on the community page

- **`CommunityDetail.razor`**: new `IsNsfw` property reads
  `CommunityDoc?.GetCommunityNsfw(ns)`. When true, a red warning banner
  ("This community contains adult content.") renders below the community card header.

### `iris:postingRestrictedToMods` — mod-only posting banner

- **`CommunityDetail.razor`**: new `IsPostingRestrictedToMods` property reads
  `CommunityDoc?.GetPostingRestrictedToMods(ns)`. When true, a warning banner
  ("Posting is restricted to moderators.") renders below the NSFW banner.

### CSS

- **`app.css`**: new styles for `.object-featured-badge`, `.object-locked-indicator`,
  `.engagement-btn--disabled`, `.community-nsfw-banner`, and `.community-mod-only-banner`.
  Uses `color-mix` for tinted backgrounds consistent with the existing design tokens.

## Files changed

| File | Change |
|---|---|
| `apps/Iris.Web.Client/Components/ObjectView.razor.cs` | +`IsLocked`, +`IsFeatured`, +`ObjectLanguage` |
| `apps/Iris.Web.Client/Components/ObjectView.razor` | Pinned badge (×2 branches), `Locked` param on `EngagementBar` (×2), `ObjectLanguage` (×2) |
| `apps/Iris.Web.Client/Components/EngagementBar.razor` | +`[Parameter] bool Locked`, disabled reply span when locked |
| `apps/Iris.Web.Client/Components/Pages/ObjectDetail.razor` | +`IsLocked`, locked indicator replaces Reply button |
| `apps/Iris.Web.Client/Components/Pages/CommunityDetail.razor` | +`IsNsfw`, +`IsPostingRestrictedToMods`, NSFW + mod-only banners |
| `apps/Iris.Web.Client/wwwroot/css/app.css` | Styles for all new UI elements |

## Tests

No new tests added (UI-only change; per web-test policy no new coded tests).
Full suite green: 1371 passed / 0 failed (Server), 106 passed (Web), 467 passed (Core),
24 passed (LiveInterop), 188 passed (Client), 38 passed (SampleServer), 20 passed (Server.Data).
No test exceeds 15 s (verified with `--blame-hang-timeout 15s`).
