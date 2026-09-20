# 14817 — Communities "Following" tab fetches followed remote communities by IRI

**Status:** done
**Slice:** Dev Queue — S4: a followed REMOTE community is missing from the Communities "Following" tab
**Owner:** dev

## Problem

The Communities page **Following** tab showed **"No communities followed yet"** for an actor who
follows a community — specifically a **remote** one (e.g. `andrew` follows the local `technology`
plus the remote `lemmy.luit.ink/c/interop`). The local communities rendered; the followed remote
community was silently dropped, even though the follow edge exists (the remote community's own page
shows an **Unfollow/Leave** button, and `Profile → Following` lists it).

## Root cause

`Communities.razor` → `ResolveFollowingCommunities()` matched each followed IRI against the **local
search cache** (`byIri.TryGetValue`, the instance's own `Group` documents) and had **no
fetch-by-IRI fallback**. Its own doc comment promised a followed community "not in the local search
(a **remote** community…)" is "fetched by its IRI", but the code never implemented that branch — so a
followed REMOTE community (not in the local search) was silently skipped.

## Fix

`apps/Iris.Web.Client/Components/Pages/Communities.razor` → `ResolveFollowingCommunities()`:

- Made the method `async Task<List<Group>>` (`ResolveFollowingCommunitiesAsync`) and awaited it at
  both call sites (`LoadAsync` and `FollowToggledAsync`).
- For each followed IRI **not** present in the local search cache, fetch the actor document by IRI
  via the shared `UiContext.GetActorAsync` seam (which routes a remote actor through the
  same-origin proxy, exactly as the directory's external lookup and the community-detail page do).
- Keep the fetched document only when it is a `Group` — a followed `Person`/`Service` is not a
  community and is filtered out, so the tab still lists communities only.
- A fetch failure (network / parse / not-a-group) is swallowed per-IRI (`try/catch → continue`):
  one unreachable remote community does not break the tab or the other cards.

This is the behavior the doc comment always described; the method now actually does it.

## Tests

Web-UI change, verified live per the web test policy (Blazor page rendering is not covered by the
in-process federation harness). `dotnet test tests/Iris.Web.Tests` → **108 pass, 0 fail** (no
regression); full solution `dotnet build` → **0 warning, 0 error**.

## Live verification (deployed)

Signed in as `andrew` (follows local `technology` + `qa-pass46-test` + `qa-pass65-test`, and remote
`lemmy.luit.ink/c/interop`): Communities → **Following** tab now shows the local communities **plus**
the remote **"Iris Interop"** community (name, description "Test community for Iris <-> Lemmy
federation interop", follower stats, and a **Leave** button — confirming the follow state). The
client's `POST /ap/v1/proxy/https%3A%2F%2Flemmy.luit.ink%2Fc%2Finterop` returns **200** (the
fetch-by-IRI the fix adds). **0 console errors.** Previously the remote community was absent
(re-confirmed 22 consecutive QA passes).

## Files

- `apps/Iris.Web.Client/Components/Pages/Communities.razor` — `ResolveFollowingCommunitiesAsync`
  fetches a followed IRI not in the local search via `Ui.GetActorAsync` and keeps `Group` results.
