# 115 — S2: Deep-linking (dead anchors → navigation)

> 2026-08-31 · Phase 8 (Sample, second round) · Slice S2 (make IRIs navigable)

## What was built

The single biggest usability win: the `ObjectView` component rendered every object/actor IRI as a **dead**
`<a href="#">`. Now every IRI is a real link so search results, feeds, outboxes, and replies are
clickable, and the object/actor pages load from a deep-link.

## The changes

### 1. `ObjectView.razor` — render real links (exercising the `IriExtensions` helpers)

- **Object IRI** → a link to the explorer's object page: `/object?iri={escaped-iri}` (uses
  `Uri.EscapeDataString`).
- **Author** (the first `attributedTo` entry, resolved via
  `ResolveObjectIri`) → a link to the actor page: `/actor?iri={escaped-iri}`, displayed as the username
  (the IRI path's last segment).
- **Parent** (a reply's `inReplyTo`, via `IriExtensions.GetParentIri`) → a link to the parent object page
  (F-12 threading).
- **Mentions** (`IriExtensions.GetMentionIris`) → links to the mentioned actors' pages.
- **Attachments** (`IriExtensions.GetAttachmentIris`) → links to the media IRIs.
- The **Link-item** branch (an `ILink`) → a link to `/object?iri={href}`.

This exercises the previously-unused `ResolveObjectIri`, `GetParentIri`, `GetMentionIris`, and
`GetAttachmentIris` helpers from the gap list (SAMPLE_EXPLORER_PLAN §3.2).

**File:** `samples/SampleBlazorClient/Components/ObjectView.razor`.

### 2. `ObjectPage.razor` — load from the `?iri=` query param

Added a `[SupplyParameterFromQuery(Name = "iri")]` parameter. On `OnInitializedAsync`, when present
(and logged on), it fills the IRI input and auto-loads, so a click from anywhere navigates + loads.

**File:** `samples/SampleBlazorClient/Pages/ObjectPage.razor`.

### 3. `ActorDetail.razor` — load from the `?iri=` / `?handle=` query params

Added `[SupplyParameterFromQuery]` params for both `iri` (an actor IRI, loaded directly) and `handle`
(a preferred username, resolved on the dial base). A new `LoadByIriAsync(Iri)` method loads an actor +
feed + moderation state from an explicit IRI; the existing handle-based `LoadAsync` now delegates to it.
`OnInitializedAsync` routes to the right loader.

**File:** `samples/SampleBlazorClient/Pages/ActorDetail.razor`.

## Tests

**`tests/SampleBlazorClient.Tests/S2DeepLinkingTests.cs`** (new) — renders `ObjectView` in-process
(bUnit) and asserts the emitted `<a href>` values point at the right routes:

- `ObjectView_Note_RendersObjectLinkToObjectPage` — a note's IRI → `/object?iri=…`.
- `ObjectView_NoteWithAuthor_RendersActorLinkToActorPage` — an author IRI → `/actor?iri=…`, displayed as
  the username.
- `ObjectView_Actor_RendersObjectLinkToObjectPage` — an actor's own IRI → `/object?iri=…`.
- `ObjectView_Reply_RendersParentLinkToObjectPage` — a reply links to its parent object page
  (`GetParentIri`).
- `ObjectView_NoteWithMention_RendersMentionLinkToActorPage` — a mention → `/actor?iri=…`, displayed as
  the mentioned username (`GetMentionIris`).
- `ObjectView_NoteWithAttachment_RendersAttachmentLink` — an attachment renders a link
  (`GetAttachmentIris`).
- `ObjectView_LinkItem_RendersObjectLinkToObjectPage` — an `ILink` item → `/object?iri=…`.

## Tooling note (bUnit)

The sample's UI tests previously ran against a live `SampleServer` (TestServer) without rendering
components. S2 needed in-process component rendering, so **bUnit 2.9.0** was added (a clean `net10.0`
target + AngleSharp 1.7.0, no known vulnerability). To satisfy its transitive requirement, the central
`Microsoft.AspNetCore.Components.WebAssembly` / `.DevServer` / `Microsoft.JSInterop` versions were bumped
from `10.0.0` → `10.0.10` (a patch release; the full solution still builds with 0 warnings under
`TreatWarningsAsErrors`). bUnit 2.x deprecates `TestContext` in favor of `BunitContext`, so the tests use
`BunitContext`.

**Files:** `Directory.Packages.props`,
`tests/SampleBlazorClient.Tests/SampleBlazorClient.Tests.csproj`.

## Verification (local http path, port 8088)

- **In-process**: all 7 S2 render tests pass (the emitted `<a href>` values point at the right routes).
- **Browser (Playwright)**: logged on as `alice@localhost` →
  - **Actors page**: directory + search results render real links — actors → `/object?iri=…`, notes →
    `/object?iri=…` with `by {author}` → `/actor?iri=…`, and a reply's `in reply to {parent}` →
    `/object?iri=…`.
  - **Clicking an actor link** (the note's `by alice`) navigated to
    `/actor?iri=https://…/alice` and the actor page loaded (actor + outbox feed + moderation state).
  - **Clicking a note's object link** navigated to
    `/object?iri=https://…/alice/notes/1` and the object page auto-loaded (the note, its `by alice`
    author link, its replies, and the Like button).
- **Full solution**: `dotnet build Iris.slnx` → 0 errors / 0 warnings.
