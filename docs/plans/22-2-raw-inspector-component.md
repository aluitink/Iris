# 22.1 deep-dive — `RawInspector` shared component (US-21)

**Feeds roadmap item:** 22.0 / 22.1 (shared components). **Stories:** US-21 (the raw-JSON escape
hatch), and the raw-inspector halves of US-5 / US-6 / US-7 / US-10.

## Current shape

Every detail page that carries a loaded document inlines the same "Raw inspector" card:

- `ActorDetail.razor` holds `bool ShowRaw` + `string? RawJson`, computes `RawJson` by serializing the
  loaded document with `System.Text.Json.JsonSerializer` (`WriteIndented` +
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`), and renders a card with a
  `Show raw JSON` / `Hide raw JSON` toggle button + `<pre class="raw-json">`.
- `Community.razor` and `ObjectPage.razor` carry their own copies of the same pattern.

So the toggle state, the serialization, and the card markup are duplicated across every detail page.

## The component

`RawInspector.razor` (new, in `samples/SampleBlazorClient/Components/`).

**Parameters**

| Parameter | Type | Meaning |
|---|---|---|
| `Document` | `object?` | The loaded document to inspect (the deserialized `IObject`/`Actor`/`Group`/…). `null` → the toggle is disabled. |
| `Title` | `string?` | Optional `<h3>` (defaults to "Raw inspector"). |
| `Description` | `string?` | Optional muted intro paragraph (defaults to the "as served … escape hatch" line). |

**Behavior**

- Owns the `ShowRaw` toggle state (collapses when the document becomes null).
- Serializes `Document` lazily, only when first expanded, with `ActivityJson`-consistent options:
  `System.Text.Json.JsonSerializer` + `WriteIndented` + `UnsafeRelaxedJsonEscaping` (the same options
  the current inline code uses, so the rendered JSON is byte-identical to today). The ActivityStreams
  library types serialize through the registered converters; the sample-local `ActivityJson` is not in
  the Blazor project's reach, so the serializer is configured inline to match.
- Renders a card with the `Show raw JSON` / `Hide raw JSON` button (disabled when `Document` is null)
  and `<pre class="raw-json">` when expanded.

## Why this is the right seam

The raw inspector is a pure presentation/debugging surface over an **already-loaded, in-memory**
document — it makes **no client/server call**. There is therefore no backend change and, per the
Phase-22 testing discipline (roadmap rule 5), no new framework test; it is verified manually
(Playwright MCP) by toggling it on a loaded actor/community/object and confirming the formatted JSON.

## Adoption in this slice

This slice replaces the inline raw-inspector card in **`ActorDetail.razor`** with `<RawInspector
Document="ActorDoc" />`. The `Community` and `ObjectPage` copies are the same one-line swap, deferred
to 22.2 to keep this slice focused.
