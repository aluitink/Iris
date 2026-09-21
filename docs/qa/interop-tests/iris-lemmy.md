# Iris ↔ Lemmy Interop — Manual Test Suite

> **Iris** (fresh, `https://qa-iris-a.luit.ink`) ↔ **Lemmy 0.19** (fresh, `https://qa-lemmy.luit.ink`).
> No prior accounts or content (Lemmy's fresh bootstrap creates only the admin
> `lemmyadmin` + the site — that is expected and not "existing data"; everything else in this
> suite is created by the suite). Failures can be Iris-side **or** Lemmy-side — record which
> (wire evidence per side).
>
> **Lemmy model mapping (read before running):**
> - Iris **person** ↔ Lemmy **user** (both `Person` actors).
> - Iris **community** ↔ Lemmy **community** (both `Group` actors; Lemmy UI: "Communities").
> - Iris **post to community** ↔ Lemmy **post** (Lemmy posts are `Page`/article-shaped in AP;
>   Iris Notes posted to a community map onto Lemmy posts).
> - Iris **reply** ↔ Lemmy **comment** (Lemmy comments are AP `Note`s that reference the post).
> - Iris **like** ↔ Lemmy **upvote**; Iris **dislike** ↔ Lemmy **downvote** (score-based,
>   `iris:score`/`likedCount`-shaped — Lemmy is the only peer that emits downvotes).
> - Iris **boost** ↔ Lemmy: **no user-level boost** (Lemmy relays at community level only) —
>   L7 is a mapping probe, expected GAP.
>
> **Accounts created by this suite:** Iris: `il-user` (password `Password1`). Lemmy:
> `iluser` (password `Password1`), created via the Lemmy UI (registration must be enabled —
> see L1). Lemmy community: `ilcomm` (created by `iluser`); Iris community: `il-comm`.
> **Token convention:** post bodies embed `IL-<test>-<n>`.
> **Run order matters:** L1 → L2 → … L12.

**Lemmy UI notes (agent orientation):**
- Lemmy Web UI: login at `/createPost`-less root `/` (the landing lists communities); create
  account at `/createPost`… no — at **`/createAccount`** (or "Register" link on the login page).
- Home timeline: after login, `/` shows the local/federation timeline based on the selected view
  (Local / Subscribed / All — record which view shows federated content; "Subscribed" shows
  followed communities + users).
- Communities at `/c/<name>`; a community's posts list is its main page. User profiles at
  `/u/<name>`.
- Actions on a post: upvote/downvote arrows (score), comment button, share. On a comment:
  upvote/downvote, reply.
- **Lemmy post creation requires a URL field** (Lemmy posts are link-posts by model; the URL can
  be a placeholder like `https://example.com/il-test` — the title + body carry our tokens).
  Record that mapping: **Iris post body → Lemmy title/url**, i.e. expect Lemmy-side rendering to
  put our token in the title or link, not necessarily the body.

---

## L1 — Bootstrap accounts (both systems)

**Preconditions:** fresh stack.

**Steps:**
1. 📸 visual — `https://iris.luit.ink/` renders (no console errors 🌐).
2. Create Iris account `il-user` / `Password1` (`/register`). Signed-in assertion.
3. 📸 visual — `https://lemmy.luit.ink/` renders Lemmy's landing/login (proves proxy + UI +
   backend health). 🌐 network — zero console errors.
4. 🔌 wire — Lemmy API sanity: `fetch('https://lemmy.luit.ink/api/v3/site')` → 200 JSON with a
   `view`/`site` object (this is the health probe; the UI landing already implies it, but the
   wire check pins it).
5. Lemmy: navigate to the **registration** page (`/createAccount` or the "Register" link). If
   registration is **closed** on the fresh instance (Lemmy default can be `Pending` or `Closed`):
   the agent cannot self-serve — record `BLOCKED(registration-closed)` and note the operator
   step (admin opens registration via `/admin/settings` or API `SETTING registration_mode`).
   Do not proceed past L1 until an account can be created.
6. Create Lemmy user `iluser` / `Password1` (username, password, verify password; 📸 visual —
   record any extra fields: email is optional on local instances). Submit. If the account lands
   in **pending** state (admin approval), record BLOCKED + operator step.
7. Log in as `iluser`. 📸 visual — the logged-in home view renders.
8. 🔌 wire — Lemmy webfinger: `fetch('https://lemmy.luit.ink/.well-known/webfinger?resource=acct:iluser@lemmy.luit.ink')`
   → 200, `subject` = `https://lemmy.luit.ink/u/iluser` (Lemmy user actor IRI shape).

**Assertions:**
- L1.1 📸 both landing pages render cleanly.
- L1.2 behavioral — both accounts created + signed in (or explicit BLOCKED reason — the whole
  suite stops at L1 in that case).
- L1.3 🔌 Lemmy webfinger resolves `iluser` to `/u/iluser`.

---

## L2 — Discovery: each system resolves the other's actor + community

**Preconditions:** L1 done.

**Steps:**
1. As `il-user` on Iris: create community `il-comm` (name "IL Comm").
2. 🔌 wire — Iris: webfinger `acct:!il-comm@iris.luit.ink` → Group doc (200, `type: "Group"`).
   The Group doc must contain the fields Lemmy requires on ingest: **`published`** (the
   historically-missing field — record its presence explicitly), `inbox`, `preferredInboxUrl`
   or `endpoints.sharedInbox`, `publicKey`.
3. As `iluser` on Lemmy: create community `ilcomm` (community create page; name `ilcomm`,
   any title/description; 📸 visual — record the exact fields Lemmy demands). After creation,
   📸 visual — `/c/ilcomm` renders with `iluser` as moderator/creator.
4. 🔌 wire — Lemmy: fetch `https://lemmy.luit.ink/c/ilcomm` with
   `Accept: application/activity+json` (via `browser_evaluate`) → 200, Group doc with
   `id: https://lemmy.luit.ink/c/ilcomm`, `published` present, `publicKey` present.
5. As `il-user` on Iris: use the remote-follow / community-lookup entry point to look up
   `!ilcomm@lemmy.luit.ink` (Iris's remote-community discovery). 📸 visual — the community
   resolves and renders on Iris (name, icon, description from the Lemmy Group doc).
6. As `iluser` on Lemmy: search for the Iris actor `il-user@iris.luit.ink` (Lemmy's search bar
   supports remote lookups — 📸 visual — record whether the search resolves it; if Lemmy's
   search can't do remote webfinger, use the direct path: open
   `https://lemmy.luit.ink/u/il-user@iris.luit.ink` is **not** a valid Lemmy URL — instead
   verify resolution via the follow flow in L3; record which path worked).
7. 🌐 network — on Iris, the fetch of the Lemmy Group doc (direct or via proxy) returned 200;
   no console errors.

**Assertions:**
- L2.1 🔌 Iris Group doc is Lemmy-conformant (`published` present — the 1597 fix; sharedInbox
  present).
- L2.2 🔌 Lemmy Group doc served with `application/activity+json` + `published` + `publicKey`.
- L2.3 📸 Iris resolves + renders the remote Lemmy community (name/icon/desc, no error card).
- L2.4 behavioral — the remote-lookup path used on each side is recorded (for the finding
   template if either side fails to resolve the other).

---

## L3 — Person-follow both directions

**Preconditions:** L1 done.

**Steps:**
1. As `il-user` on Iris: follow `iluser@lemmy.luit.ink`.
2. 🔌 wire — Iris outbox: `Follow` with `object` = `https://lemmy.luit.ink/u/iluser`.
3. As `iluser` on Lemmy: ⟳ reload, open profile `/u/iluser`. 📸 visual — **Followers** count
   increased / `il-user@iris.luit.ink` visible in the followers list (Lemmy shows remote
   followers with `@name@host`).
4. As `iluser` on Lemmy: follow `il-user@iris.luit.ink` (Lemmy: search the remote user — if
   search works (L2.6), click the result → profile → "Follow"; if not, record the blocker and
   use Lemmy's follow-by-URL if available).
5. As `il-user` on Iris: ⟳ reload. 📸 visual — the inbound follow is visible. **Gate check:**
   if the Iris account is set to manually approve, a pending request appears — Accept it and
   record the gate (this exercises the same path as Iris↔Iris A3, against Lemmy). If auto-accept,
   the edge is immediate — record which.
6. 🔌 wire — Iris: inbound `Follow` recorded (followers surface / collection contains
   `https://lemmy.luit.ink/u/iluser`); if accepted, `Accept` in Iris's outbox targeting the
   Follow IRI.
7. As `iluser` on Lemmy: ⟳ reload profile. 📸 visual — following list shows the Iris user.

**Assertions:**
- L3.1 🔌 Iris `Follow` → Lemmy actor IRI (wire).
- L3.2 📸 Lemmy shows the Iris user as follower (remote identity with host).
- L3.3 behavioral — inbound follow from Lemmy recorded on Iris; Accept delivered if gated
   (record the gate behavior).
- L3.4 📸 edge visible on both sides after settle (+10 s retry).

---

## L4 — Community join: Iris user joins Lemmy community

**Preconditions:** L2 + L3 done (`ilcomm` exists on Lemmy).

**Steps:**
1. As `il-user` on Iris: join/follow `!ilcomm@lemmy.luit.ink` (the remote-community follow
   entry point — the cached-remote-community Follow path fixed in 1597; this is the regression
   check for that fix).
2. 🔌 wire — Iris outbox: `Follow` with `object` = `https://lemmy.luit.ink/c/ilcomm`.
3. As `iluser` on Lemmy: ⟳ reload `/c/ilcomm`. 📸 visual — **member/follower count increased**;
   the community page shows the new (remote) member if Lemmy lists members with remote hosts.
4. 🔌 wire — Lemmy: `GET https://lemmy.luit.ink/c/ilcomm` (activity+json) — the Group's
   `followers` (or `iris:`-equivalent membership collection) contains
   `https://iris.luit.ink/ap/v1/u/il-user` (record the exact collection IRI used).
5. **Gated variant (optional, if time allows):** create a second Lemmy community `ilcomm2` with
   "Follow requests" enabled (Lemmy's per-community setting), join it from Iris, verify the
   request is **pending** on Lemmy (📸 visual — the mod/pending indicator), then Accept as
   `iluser` (Lemmy community settings → approve) and verify the edge finalizes on both sides.

**Assertions:**
- L4.1 🔌 Follow (join) delivered to the Lemmy community IRI (wire).
- L4.2 📸 Lemmy member count reflects the Iris join (visual proof of the 1597 delivery fix).
- L4.3 🔌 Lemmy's Group doc membership/followers collection contains the Iris actor IRI.
- L4.4 (optional) 📸 gated community: pending → approve → finalized, both sides.

---

## L5 — Community join: Lemmy user joins Iris community

**Preconditions:** L2 + L3 done (`il-comm` exists on Iris).

**Steps:**
1. As `iluser` on Lemmy: join `!il-comm@iris.luit.ink` (Lemmy's remote-community join — search
   or follow-by-handle; 📸 visual — record the exact Lemmy UI path used).
2. As `il-user` on Iris: ⟳ reload the community page `/c/il-comm`. 📸 visual — member count
   increased; `iluser@lemmy.luit.ink` visible in the members list (remote identity).
3. 🔌 wire — Iris: the community's followers/members collection contains
   `https://lemmy.luit.ink/u/iluser`.
4. If the Iris community is **gated** (`manuallyApprovesMembers`): verify a pending request
   appears in Iris's community requests surface, Accept it, and re-verify (this is the Iris-side
   gate against a Lemmy joiner). Record which path occurred.

**Assertions:**
- L5.1 📸 Iris community member count + member list reflect the Lemmy join.
- L5.2 🔌 Iris membership collection contains the Lemmy actor IRI.
- L5.3 behavioral — gate behavior (if any) recorded: pending → accept → finalized.

---

## L6 — Post to community: Iris → Lemmy (the content-federation proof)

**Preconditions:** L4 done (Iris user is a member of `ilcomm`).

**Steps:**
1. As `il-user` on Iris: post to the **remote** community `!ilcomm@lemmy.luit.ink`:
   body `IL-L6-1 iris post into lemmy community`. (Iris's post-to-community control with the
   remote community selected — 📸 visual — record how the remote community is picked.)
2. 🔌 wire — Iris outbox: `Create` with `object` = Note, `content` contains `IL-L6-1`,
   `attributedTo`/`to`/`cc` reference the Lemmy community Group IRI (record the exact shape).
3. As `iluser` on Lemmy: ⟳ reload `/c/ilcomm`. 📸 visual — the post appears in the community:
   - our token `IL-L6-1` is visible (title and/or link text — per the Lemmy link-post mapping
     note in the header, the token may surface as the post **title** or URL; record exactly
     where),
   - author shown as `il-user@iris.luit.ink` (remote user with host),
   - the post is clickable into its detail view.
4. Open the post detail on Lemmy. 📸 visual — content renders (body text intact or the
   title/URL mapping holds); author profile link works (📸 visual — clicking through shows the
   Iris user's profile as Lemmy renders it: name, avatar).
5. **Lemmy→Iris direction:** as `iluser` on Lemmy: create a post in `ilcomm`: title
   `IL-L6-2 lemmy post`, URL `https://example.com/il-l6-2`.
6. As `il-user` on Iris: ⟳ reload the `!ilcomm@lemmy.luit.ink` community page. 📸 visual — the
   post appears: title `IL-L6-2 lemmy post`, author `iluser@lemmy.luit.ink`. 📸 visual — open
   its detail on Iris: the URL is shown, content sane (no raw-JSON leakage), author identity
   correct.
7. 🔌 wire — the Lemmy post is fetchable as activity+json
   (`https://lemmy.luit.ink/post/<id>` with `Accept: application/activity+json` — get the IRI
   from Iris's stored object or the Lemmy post URL) → 200, `content`/`name` contains our token.

**Assertions:**
- L6.1 🔌 Iris `Create` references the Lemmy community IRI (wire shape recorded).
- L6.2 📸 Iris post visible on Lemmy in the community, token visible, remote author identity
  correct (the headline content-federation proof).
- L6.3 📸 Lemmy post visible on Iris in the community feed + detail (title, URL, author).
- L6.4 🔌 Lemmy post fetchable by IRI with activity+json.

---

## L7 — Replies (Iris comment ↔ Lemmy comment)

**Preconditions:** L6 done (the `IL-L6-2` Lemmy post exists on both sides).

**Steps:**
1. As `il-user` on Iris: open the `IL-L6-2` post detail → reply: `IL-L7-1 iris comment`.
   🔌 wire — Iris outbox: `Create` with `object` = Note, `inReplyTo` = the Lemmy post IRI,
   community/`cc` referencing the Lemmy community.
2. As `iluser` on Lemmy: ⟳ reload the post detail. 📸 visual — the comment appears under the
   post: text `IL-L7-1 iris comment`, author `il-user@iris.luit.ink`, comment count increased.
3. As `iluser` on Lemmy: reply to that comment: `IL-L7-2 lemmy reply`.
4. As `il-user` on Iris: ⟳ reload the post detail. 📸 visual — the thread shows both levels:
   Iris comment → Lemmy reply nested, correct authors.
5. **Nested depth:** as `il-user` on Iris: reply to the Lemmy reply: `IL-L7-3 iris nested`.
   On Lemmy: 📸 visual — 3-level thread renders (Lemmy handles nesting natively; record how the
   remote author renders at depth 3).

**Assertions:**
- L7.1 🔌 Iris comment's `inReplyTo` = Lemmy post IRI (wire).
- L7.2 📸 Iris comment visible as a Lemmy comment (author + count).
- L7.3 📸 Lemmy reply visible on Iris, nested; 3-level thread renders on both.

---

## L8 — Votes: upvote + downvote (Lemmy's score model)

**Preconditions:** L6 done.

**Steps:**
1. As `iluser` on Lemmy: **upvote** the `IL-L6-2` post (up arrow). 📸 visual — score changes
   (0 → 1), arrow highlighted.
2. As `il-user` on Iris: ⟳ reload the post detail. 📸 visual — the vote count / score reflects
   the upvote (Iris shows `likedCount`/score — record the exact rendering; if Iris shows no vote
   count for this post shape, record the rendering gap).
3. As `iluser` on Lemmy: **downvote** the same post. On Iris: ⟳ reload. 📸 visual — score
   reflects the downvote (Lemmy's model: upvote then downvote on the same post = net 0 or
   -1 depending on prior state — record the actual numbers both sides show; the assertion is
   *consistency*, not a specific value).
4. As `il-user` on Iris: **like** the post (Iris's like control). On Lemmy: ⟳ reload. 📸 visual
   — score increased by 1 (Iris like → Lemmy upvote).
5. As `il-user` on Iris: **dislike** (if the vote bar offers it for this post — the 13827 note
   says the vote bar gates on Lemmy-IRI-shaped posts; this post IS Lemmy-shaped, so the bar
   should be present — 📸 visual — record whether the dislike control exists). On Lemmy: ⟳
   reload — score reflects the downvote.
6. 🔌 wire — Iris outbox: a `Like` (and `Dislike`/`iris:`-score activity if offered) targeting
   the Lemmy post IRI; an `Undo`-like for the unlike. Record exact activity shapes.

**Assertions:**
- L8.1 📸 Lemmy upvote reflected in Iris's score rendering (inbound vote).
- L8.2 📸 Lemmy downvote reflected (score consistency both sides; exact semantics recorded).
- L8.3 📸 Iris like → Lemmy upvote (score +1 on Lemmy).
- L8.4 📸 Iris dislike control present for a Lemmy-shaped post (the 13827 gate) → Lemmy
   downvote reflected. (If the control is absent: record the 13827 follow-up still-open.)
- L8.5 🔌 vote activity shapes recorded (Like/Dislike/Undo on Iris outbox).

---

## L9 — Boost mapping probe (expected GAP)

**Preconditions:** L6 done.

**Steps:**
1. As `il-user` on Iris: attempt to **boost** the `IL-L6-2` post (Iris's boost control).
2. 🔌 wire — Iris outbox: an `Announce` targeting the Lemmy post IRI is published (record it).
3. As `iluser` on Lemmy: ⟳ reload the post/community. 📸 visual — **expected:** no user-boost
   concept appears (Lemmy has no per-user reblog for posts; community relay is the only
   "boost"-like). Record the actual behavior: if Lemmy errors, drops the `Announce` (check
   Lemmy logs if accessible — otherwise record the delivery status from Iris's side: 202 vs
   4xx), or renders something — all are valid observations.
4. As `iluser` on Lemmy: verify the post is still intact and unmodified (an `Announce` must not
   corrupt the post).

**Assertions:**
- L9.1 🔌 Iris publishes the `Announce` (outbound capability works regardless of peer support).
- L9.2 behavioral — Lemmy's response to the `Announce` is **recorded** (accepted-and-ignored /
  4xx / relayed) — PASS = no corruption + behavior documented; a 5xx or post corruption = FAIL.
- (Expected outcome: documented GAP — Lemmy has no user boost. Do not file a finding for the
  GAP itself; file one only for corruption or unhandled errors.)

---

## L10 — Edit propagation

**Preconditions:** L6 done (use `IL-L6-2`).

**Steps:**
1. As `iluser` on Lemmy: edit the post (edit button on own post): title `IL-L6-2 EDITED`.
   🔌 wire — Lemmy: the post IRI now returns the updated `Page`/Note (fetch with
   activity+json: `name`/`content` contains `IL-L6-2 EDITED`, `updated` > `published`); an
   `Update` activity is in the Lemmy actor/community outbox.
2. As `il-user` on Iris: ⟳ reload the post detail. 📸 visual — the edited title/content is
   shown (stale copy = FAIL).
3. As `il-user` on Iris: edit the `IL-L6-1`-era Iris post if it still exists (or create + edit a
   fresh post in `ilcomm`: `IL-L10-1 iris edit test` → `IL-L10-1 EDITED`).
4. As `iluser` on Lemmy: ⟳ reload `/c/ilcomm`. 📸 visual — the edited version renders.

**Assertions:**
- L10.1 🔌 Lemmy `Update` served on the post IRI (wire).
- L10.2 📸 Lemmy edit visible on Iris (no stale copy).
- L10.3 📸 Iris edit visible on Lemmy.

---

## L11 — Delete propagation

**Preconditions:** L10 done.

**Steps:**
1. As `iluser` on Lemmy: **delete** the `IL-L6-2 EDITED` post. 🔌 wire — the Lemmy post IRI now
   returns 404 or a Tombstone (record which); a `Delete` in the outbox.
2. As `il-user` on Iris: ⟳ reload the community page. 📸 visual — the post is gone (or a
   tombstone/"deleted" placeholder — record which; stale live copy = FAIL).
3. As `il-user` on Iris: delete one of Iris's posts in `ilcomm` (e.g. the `IL-L10-1 EDITED`
   post). 🔌 wire — Iris outbox: `Delete`; Note IRI 404/Tombstone.
4. As `iluser` on Lemmy: ⟳ reload `/c/ilcomm`. 📸 visual — post gone/tombstoned.

**Assertions:**
- L11.1 🔌 Lemmy delete: IRI 404/Tombstone + `Delete` activity.
- L11.2 📸 deletion visible on Iris.
- L11.3 🔌 + 📸 Iris delete visible on Lemmy.

---

## L12 — Unfollow/leave both directions + final state

**Preconditions:** L3/L4/L5 done (person follows + community joins in both directions).

**Steps:**
1. As `il-user` on Iris: unfollow `iluser@lemmy.luit.ink` and leave `!ilcomm@lemmy.luit.ink`.
   🔌 wire — Iris outbox: `Undo(Follow)` for the person + `Undo(Follow)` (leave) for the
   community IRI.
2. As `iluser` on Lemmy: ⟳ reload `/u/iluser` (followers) and `/c/ilcomm` (members). 📸 visual —
   both counts decreased; the Iris user no longer listed.
3. As `iluser` on Lemmy: unfollow `il-user@iris.luit.ink` and leave `!il-comm@iris.luit.ink`.
4. As `il-user` on Iris: ⟳ reload. 📸 visual — follower + community-member surfaces no longer
   show the Lemmy user.
5. **Feed-cutoff check:** as `iluser` on Lemmy: post `IL-L12-1 after unfollow` in `ilcomm`.
   As `il-user` on Iris: ⟳ reload home. 📸 visual — the post does **not** appear (Iris user
   left the community + unfollowed the user — feed must respect both removed edges; a stale
   delivery = FAIL, this catches the unfollow-fanout bug class).
6. 🔌 wire — final state: Iris followers/following + community membership collections contain no
   Lemmy IRIs; Lemmy side symmetric (record collection fetches).

**Assertions:**
- L12.1 🔌 `Undo(Follow)` × 2 from Iris (person + community), correct object IRIs.
- L12.2 📸 Lemmy counts/lists reflect both removals.
- L12.3 📸 Iris surfaces reflect the Lemmy-side removals.
- L12.4 behavioral — after leave + unfollow, new Lemmy community posts do **not** reach Iris
   home (feed cutoff honored).

---

## Known-behavior notes (record, don't fail)

- **Lemmy signature strictness:** historically Lemmy's HTTP-signature parser has rejected some
  Iris outbound deliveries (the 136.3-era K1 gap). If **any** outbound delivery to Lemmy 401/400s
  while the identical delivery to Mastodon (M-suite) succeeds, that is a **finding** (Iris
  signature conformance), not a Lemmy quirk — record the signature header + response body.
- **`published` field:** L2.1 re-checks the 1597 fix (`published` on actor + community docs).
  Its absence = immediate regression finding.
- **Lemmy post = link post:** the title/URL mapping (header note) means "body visible on Lemmy"
  assertions must accept the token appearing in **title or URL** — only require the token to be
  *visible somewhere* in the Lemmy rendering, and record exactly where.
- **Lemmy federation lag:** Lemmy processes inbound activities via its worker queue; one reload
  + 10 s retry is standard, but allow one extra 15 s retry for community-feed assertions
  (L4/L6/L12) before calling them stale.
- **Registration/gating BLOCKED states** (L1/L4.5): a BLOCKED result names the exact operator
  step; the suite pauses, not fails, at that point.

## Run log

| Test | Result | Evidence (screenshot paths / JSON values) | Date |
|---|---|---|---|
