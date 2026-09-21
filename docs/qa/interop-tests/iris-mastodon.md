# Iris ↔ Mastodon Interop — Manual Test Suite

> **Iris** (fresh, `https://qa-iris-a.luit.ink`) ↔ **Mastodon 4.7** (fresh,
> `https://qa-mastodon.luit.ink`). No prior accounts or content. Failures here can be Iris defects
> **or** Mastodon-side behavior — the agent must record **which side** misbehaves (wire evidence:
> what each side's outbox/collection actually contains, plus what each UI renders).
>
> **Accounts created by this suite:** Iris: `im-user` (password `Password1`). Mastodon:
> `imuser` (password `Password1` — Mastodon requires 8+ chars; adjust if the form rejects,
> record the password used). Community: Iris `im-comm`; Mastodon `imcomm` (Mastodon calls these
> "hashtags" — the `#imcomm` hashtag is the joinable community surrogate; record the mapping).
> **Token convention:** post bodies embed `IM-<test>-<n>`.
> **Run order matters:** M1 → M2 → … M12.

**Mastodon UI notes (agent orientation):**
- Mastodon's Web UI: timelines at `/` (home = follow feed), `/public` (federated), profiles at
  `/@<handle>`. Post = the composer at top of home. Actions on a post (like/boost/reply) are the
  icon row under each status (comment = reply, heart = favorite, reblog icon = boost).
- Mastodon "follows" are **auto-accepted** by default (no pending state) — Iris follows of a
  Mastodon user should finalize without operator action.
- Remote actors on Mastodon render with their `@handle@host` mention form in content.

---

## M1 — Bootstrap accounts (both systems)

**Preconditions:** fresh stack.

**Steps:**
1. 📸 visual — `https://iris.luit.ink/` renders (Iris landing, no console errors 🌐).
2. Create Iris account `im-user` / `Password1` (`/register`). Signed-in assertion.
3. 📸 visual — `https://mastodon.luit.ink/` renders Mastodon's login page (the stock login,
   proving the proxy + SPA are healthy). 🌐 network — zero console errors on load.
4. Mastodon: navigate to `/signup` (link on the login page — if registration is disabled on this
   instance, this test suite is **BLOCKED**: record it, do not proceed). Fill username `imuser`,
   password `Password1` (accept the terms/checkboxes; 📸 visual — record any extra fields
   Mastodon 4.7 demands). Submit; complete any email-confirmation step (local instance: record
   whether confirmation blocks sign-in — if so, note the operator step required and mark M1
   `BLOCKED(email-confirm)` until resolved).
5. Log in as `imuser`. 📸 visual — home timeline renders (empty on a fresh instance).
6. 🔌 wire — `fetch('https://mastodon.luit.ink/.well-known/webfinger?resource=acct:imuser@mastodon.luit.ink')`
   → 200, `subject` = `https://mastodon.luit.ink/users/imuser`.

**Assertions:**
- M1.1 📸 both landing/login pages render cleanly.
- M1.2 behavioral — both accounts created + signed in (or explicit BLOCKED reason).
- M1.3 🔌 Mastodon webfinger resolves `imuser` to the actor IRI.

---

## M2 — Iris follows Mastodon user (outbound Follow → their Accept)

**Preconditions:** M1 done.

**Steps:**
1. As `im-user` on Iris: use the remote-follow entry point to follow `imuser@mastodon.luit.ink`.
2. ⟳ reload Iris. 📸 visual — `imuser@mastodon.luit.ink` appears in following; no pending-request
   state (Mastodon auto-accepts).
3. 🔌 wire — Iris: `im-user`'s outbox contains a `Follow` with `object` =
   `https://mastodon.luit.ink/users/imuser`.
4. As `imuser` on Mastodon: ⟳ reload, open **Followers** (profile page `/@imuser` → Followers
   tab). 📸 visual — `im-user@iris.luit.ink` is listed.
5. 🌐 network — on Iris, the fetch of Mastodon's actor doc (direct or via Iris proxy) succeeded
   (200); record the request pattern.

**Assertions:**
- M2.1 🔌 Follow on Iris's outbox with correct object IRI.
- M2.2 📸 Mastodon shows the Iris account as a follower (cross-system identity: host shown).
- M2.3 behavioral — no pending state on Iris (auto-accept path works for a Mastodon peer).

---

## M3 — Mastodon user follows Iris (inbound Follow → Iris Accept)

**Preconditions:** M2 done (either direction establishes the edge; this test exercises the
**inbound** leg specifically — if M2 already created a mutual edge, use a second Iris account
`im-user2` for this test and record the adaptation).

**Steps:**
1. As `imuser` on Mastodon: navigate to `https://mastodon.luit.ink/` and use the search bar →
   search `im-user@iris.luit.ink` (Mastodon's remote lookup via webfinger). Click the account →
   profile. 📸 visual — the profile renders: name, avatar, bio (from Iris's actor doc), follower
   count. **This is the key discovery assertion**: Mastodon resolved our actor over the wire.
2. Click **Follow**.
3. As `im-user` on Iris: ⟳ reload. 📸 visual — follow visible (followers surface or
   notification — record which; if Iris auto-accepts, no pending state; if Iris is gated, Accept
   it and record the gate).
4. 🔌 wire — Iris: the inbound `Follow` is recorded (notification or followers collection
   contains `https://mastodon.luit.ink/users/imuser`); if accepted, Iris's outbox contains an
   `Accept` whose `object` is the Follow IRI.
5. As `imuser` on Mastodon: ⟳ reload profile → Following tab. 📸 visual — `im-user@iris.luit.ink`
   listed as following.

**Assertions:**
- M3.1 📸 Mastodon's remote search resolves the Iris actor and renders the full profile
   (name + avatar + summary, not an error/placeholder).
- M3.2 🔌 inbound Follow recorded on Iris; Accept delivered (if auto-accept, the edge is
   finalized without operator action — record which).
- M3.3 📸 edge visible on both sides.

---

## M4 — Iris posts → Mastodon renders it (the core "post federates" proof)

**Preconditions:** M2/M3 done (at least one direction of follow exists; the post must reach
`imuser`'s timeline — if only M3 (they follow us) exists, the post appears in their home).

**Steps:**
1. As `im-user` on Iris: compose a **public** post: `IM-M4-1 iris to mastodon hello`. Publish.
   📸 visual — post appears in Iris home.
2. 🔌 wire — Iris outbox: `Create` with `Note`, `content` contains `IM-M4-1`, audience includes
   public (`as:Public` / `https://www.w3.org/ns/activitystreams#Public`).
3. As `imuser` on Mastodon: ⟳ reload home timeline. 📸 visual — the post appears:
   - body contains `IM-M4-1 iris to mastodon hello` (HTML rendering intact: newlines, no raw
     markup leakage — 📸 visual check),
   - author shown as `im-user` with `@im-user@iris.luit.ink` (or the avatar + display name from
     the Iris actor doc),
   - the post links back to the Iris object IRI (Mastodon shows the original post URL — click
     through: 📸 visual — the Iris object page renders).
4. If the post does **not** appear after one reload + 10 s retry: capture evidence —
   `browser_network_requests` on Iris (was the `Create` delivery attempted? to which inbox?
   shared-inbox or personal inbox?), and the Iris console. This is the headline test; a FAIL
   here gets a finding immediately.
5. Also check Mastodon's **federated/public timeline** (`/public`): the post should be visible
   there too (public post). 📸 visual.

**Assertions:**
- M4.1 🔌 `Create` + public audience on Iris outbox.
- M4.2 📸 post renders in the follower's home timeline with correct body + author identity.
- M4.3 📸 post visible on Mastodon's public/federated timeline.
- M4.4 behavioral — the post's link targets the Iris object IRI and that page renders (click-through).

---

## M5 — Mastodon posts → Iris receives + renders

**Preconditions:** M2/M3 done (Iris follows `imuser` so the post reaches Iris's home).

**Steps:**
1. As `imuser` on Mastodon: compose a public toot: `IM-M5-1 mastodon to iris hello`. Publish.
2. 🔌 wire (Iris side, after ⟳) — the inbound `Create` was accepted (Iris home shows it; the
   object is fetchable: `fetch(<the Note IRI on mastodon.luit.ink>)` from the Iris origin → 200,
   `application/activity+json` or `ld+json`, `content` contains `IM-M5-1`).
3. As `im-user` on Iris: ⟳ reload home. 📸 visual — the toot appears:
   - body `IM-M5-1 mastodon to iris hello`,
   - author `imuser@mastodon.luit.ink` (remote identity, avatar from Mastodon),
   - rendered HTML is sane (no unescaped entities, no layout breakage — 📸 visual).
4. Open the post detail. 📸 visual — full post; author profile link works → navigating to the
   author's profile on Iris shows the **Mastodon profile** (name, avatar, bio) rendered from the
   remote actor doc. 🌐 network — the remote actor fetch succeeded (200) with no console errors.
5. Add a **hashtag** dimension: as `imuser` on Mastodon, post `IM-M5-2 with #testtag`. On Iris
   home: 📸 visual — the hashtag renders (as a link or plain `#testtag` — record the form; if
   Iris renders a broken tag entity, FAIL).

**Assertions:**
- M5.1 🔌 inbound `Create` accepted + Note fetchable by IRI from Iris's origin (proxy path works).
- M5.2 📸 toot renders in Iris home with correct body + remote author identity.
- M5.3 📸 remote author's profile renders on Iris (name/avatar/bio from the Mastodon actor doc).
- M5.4 📸 hashtag content doesn't corrupt rendering.

---

## M6 — Reply threading (Iris → Mastodon → Iris)

**Preconditions:** M4 done (the `IM-M4-1` post exists on both sides).

**Steps:**
1. As `imuser` on Mastodon: on the `IM-M4-1` post in home, click the **reply** icon (comment
   icon). Compose `IM-M6-1 reply from mastodon`. Send.
2. As `im-user` on Iris: ⟳ reload the object detail of `IM-M4-1`. 📸 visual — the reply appears
   in the thread under the parent, author `imuser@mastodon.luit.ink`, body `IM-M6-1…`.
   🔌 wire — the reply's Note (Mastodon IRI) has `inReplyTo` = the Iris Note IRI.
3. As `im-user` on Iris: reply to that reply: `IM-M6-2 reply from iris`.
4. As `imuser` on Mastodon: ⟳ reload the `IM-M4-1` thread (open the post → "view conversation").
   📸 visual — the 3-level conversation renders: Iris post → Mastodon reply → Iris reply,
   nested, with both remote identities correct.

**Assertions:**
- M6.1 🔌 `inReplyTo` IRI chain is correct across platforms.
- M6.2 📸 conversation renders on **both** platforms, all 3 levels, correct author per level.

---

## M7 — Favorite (like) both directions

**Preconditions:** M4 + M5 done.

**Steps:**
1. As `imuser` on Mastodon: **favorite** the `IM-M4-1` post (heart icon). 📸 visual — heart
   filled/colored.
2. As `im-user` on Iris: ⟳ reload the `IM-M4-1` detail. 📸 visual — like count is 1 (and the
   liker attributable if Iris shows it).
   🔌 wire — Iris received a `Like` (it is reflected in the count; the activity is in Iris's
   received state — the count is the UI-observable proof).
3. As `im-user` on Iris: **like** the `IM-M5-1` post.
4. As `imuser` on Mastodon: ⟳ reload. 📸 visual — `IM-M5-1`'s favorite count is 1.
5. Unfavorite both directions; ⟳ reload both; 📸 visual — counts back to 0.

**Assertions:**
- M7.1 📸 Iris like count increments from a Mastodon favorite (inbound `Like`).
- M7.2 📸 Mastodon favorite count increments from an Iris like (outbound `Like` accepted).
- M7.3 📸 both directions decrement on unlike.

---

## M8 — Boost (reblog / Announce)

**Preconditions:** M4 done.

**Steps:**
1. As `imuser` on Mastodon: **boost** the `IM-M4-1` post (reblog icon). 📸 visual — boost icon
   active; the boost appears in `imuser`'s home timeline as a reblog card.
2. 🔌 wire — Mastodon's outbox for `imuser` (fetch
   `https://mastodon.luit.ink/users/imuser/outbox` — public GET) contains an `Announce` with
   `object` = the Iris Note IRI.
3. As `im-user` on Iris: ⟳ reload the `IM-M4-1` detail / notifications. 📸 visual — the boost is
   surfaced (boost count / "reblogged by" — record the form; if Iris has no boost UI surface,
   record as GAP and rely on the wire: the `Announce` must at minimum have been **accepted**
   (no 401/400 on delivery — check Iris console + network)).
4. As `im-user` on Iris: **boost** the `IM-M5-1` post (Iris's boost/repost control).
5. 🔌 wire — Iris outbox contains an `Announce` with `object` = the Mastodon Note IRI.
6. As `imuser` on Mastodon: ⟳ reload home. 📸 visual — the Iris boost appears as a reblog of
   `IM-M5-1` (boost by `im-user@iris.luit.ink`).
7. Unboost both; verify removal.

**Assertions:**
- M8.1 🔌 Mastodon `Announce` targeting the Iris Note (wire).
- M8.2 📸 Iris surfaces the inbound boost (or recorded GAP with wire-accept evidence).
- M8.3 🔌 Iris `Announce` targeting the Mastodon Note (wire).
- M8.4 📸 Mastodon renders the Iris boost as a reblog in the timeline.

---

## M9 — Edit (Update) propagation

**Preconditions:** M4 done (`IM-M4-1` on both sides).

**Steps:**
1. As `im-user` on Iris: edit `IM-M4-1` → `IM-M4-1 EDITED from iris`.
   🔌 wire — Iris outbox: `Update` activity, `object` = the Note, new `content`, `updated` >
   `published`.
2. As `imuser` on Mastodon: ⟳ reload home / the post. 📸 visual — body shows
   `IM-M4-1 EDITED from iris`. (Mastodon updates federated statuses in place; a stale body is a
   FAIL — note: Mastodon may keep the old version visible if the `Update` was malformed; record
   what renders.)
3. As `imuser` on Mastodon: edit their `IM-M5-1` → `IM-M5-1 EDITED from mastodon`.
4. As `im-user` on Iris: ⟳ reload. 📸 visual — edited body visible.

**Assertions:**
- M9.1 📸 Iris edit visible on Mastodon.
- M9.2 📸 Mastodon edit visible on Iris.

---

## M10 — Delete propagation (tombstone)

**Preconditions:** M9 done (use the edited `IM-M4-1`).

**Steps:**
1. As `im-user` on Iris: delete `IM-M4-1`. 🔌 wire — Iris outbox: `Delete` with `object` = Note
   IRI; the Note IRI on Iris now returns 404 or a `Tombstone`.
2. As `imuser` on Mastodon: ⟳ reload. 📸 visual — the post is gone from the timeline, **or**
   shows a "[deleted]" placeholder (record which; a live stale copy = FAIL).
3. As `imuser` on Mastodon: delete their `IM-M5-1`.
4. As `im-user` on Iris: ⟳ reload. 📸 visual — post gone (or tombstone placeholder).

**Assertions:**
- M10.1 🔌 `Delete` published on Iris; Note IRI 404/Tombstone.
- M10.2 📸 Mastodon reflects the deletion (no stale live copy).
- M10.3 📸 Iris reflects the Mastodon deletion.

---

## M11 — Community surrogate: hashtag join + post visibility

**Preconditions:** M2/M3 done. Mastodon has no "communities" — the interop surrogate is the
**hashtag** (`#imcomm`) + Mastodon's hashtag timelines. This test records how Iris's community
concept maps onto Mastodon's hashtag world.

**Steps:**
1. As `im-user` on Iris: create community `im-comm`.
2. As `imuser` on Mastodon: post a public toot: `IM-M11-1 in #imcomm`.
   📸 visual — it appears on Mastodon's hashtag timeline
   (`https://mastodon.luit.ink/tags/imcomm`).
3. As `im-user` on Iris: the post (with `#imcomm` in its content) — does it appear anywhere in
   `im-comm`'s feed on Iris? 📸 visual + record the actual behavior (expected: it does NOT
   automatically join the Iris community — Mastodon hashtags and Iris communities are different
   objects; **record the mapping gap explicitly** as the test's finding if that is what happens).
4. As `im-user` on Iris: post to `im-comm`: `IM-M11-2 iris community post`.
   🔌 wire — the Note's `tag` array: does it include the community's IRI? (Iris community posts
   tag the community.) Record the exact `tag` shape.
5. As `imuser` on Mastodon: ⟳ reload home. 📸 visual — does the community post render? What does
   the community tag look like to Mastodon (a link to the Iris community? a plain tag? dropped)?
   **Record whatever renders** — this test is a mapping-behavior probe; the pass criterion is
   that nothing *breaks* (no 401 on delivery, no rendering corruption), not that the concepts
   unify.

**Assertions:**
- M11.1 📸 `#imcomm` toot visible on Mastodon's hashtag timeline.
- M11.2 behavioral + 📸 — cross-post visibility behavior is **recorded** (community≠hashtag
  mapping documented; a silent drop of the post on either side is a FAIL, a documented
  non-mapping is PASS-with-note).
- M11.3 📸 community post renders on Mastodon without corruption (tag shape recorded).

---

## M12 — Unfollow both directions + final state

**Preconditions:** M2/M3/M7/M8 done.

**Steps:**
1. As `im-user` on Iris: unfollow `imuser@mastodon.luit.ink`.
   🔌 wire — Iris outbox: `Undo(Follow)`.
2. As `imuser` on Mastodon: ⟳ reload Followers tab. 📸 visual — `im-user@iris.luit.ink` gone.
3. As `imuser` on Mastodon: unfollow `im-user@iris.luit.ink` (Following tab → unfollow).
4. As `im-user` on Iris: ⟳ reload. 📸 visual — the edge is gone from the followers surface;
   Mastodon's subsequent posts no longer enter Iris's follow feed (post one more
   `IM-M12-1 after unfollow` and verify it does **not** appear in Iris home; if Iris still shows
   it, record as FAIL — stale follow feed after Undo).
5. 🔌 wire — Iris: no remaining edges to/from `https://mastodon.luit.ink/users/imuser`
   (followers/following collections).

**Assertions:**
- M12.1 🔌 `Undo(Follow)` from Iris; Mastodon drops the follower.
- M12.2 📸 unfollow on Mastodon side removes the edge on Iris.
- M12.3 behavioral — after mutual unfollow, new Mastodon posts do **not** reach Iris's follow
   feed (feed respects the removed edge — this catches the known "unfollow fanout" class of bug).

---

## Known-behavior notes (record, don't fail)

- **Mastodon `sensitive`/`spoilerText`:** if time allows, have `imuser` post with content
  warning + media; verify Iris renders the warning without crashing (extended-type round-trip).
- **Shared inbox:** Mastodon prefers `preferred_inbox_url` / shared inbox for some deliveries.
  Watch Iris's console/network during M4–M10: if a delivery to the shared inbox returns 401/400,
  that's a finding (signature or routing), not a peer issue.
- **Mastodon rate limits / queueing:** federation from Mastodon can lag seconds to a minute.
  One reload + 10 s retry is the standard wait; beyond that, record `SLOW` with timestamps.

## Run log

| Test | Result | Evidence (screenshot paths / JSON values) | Date |
|---|---|---|---|
| M1 | **PARTIAL — Mastodon side BLOCKED(agent-credential)** | **Iris `im-user`:** created via `/register` + signed in (profile shows `im-user`/`IM User`, `Log out` present). **Mastodon `imuser`:** could NOT be created — `/api/v1/instance` reports `registrations: false`, `invites_enabled: true` (invite-only); signup routes 404 (`/users/sign_up`, `/signup`, `/auth/users/sign_up`, `/sign_up`, `POST /users`); `/invites` → 302 → `/auth/sign_in`. A **pre-seeded** `imuser` already exists (`stats.user_count: 1`; webfinger resolves) but its password is unknown → **Mastodon UI undrivable by the agent**. **M1.3 PASS:** webfinger `acct:imuser@qa-mastodon.luit.ink` → 200, `self` = `https://qa-mastodon.luit.ink/ap/users/117306213651189335`. **M1.1:** Iris landing clean (0 console errors); Mastodon renders (explore + login). **M1.2 PARTIAL:** Iris account created+signed-in; Mastodon account pre-exists but agent cannot sign in (invite-only, no password). **Note:** the suite assumed a fresh Mastodon instance — this cluster ships a pre-seeded `imuser` with invite-only registration. | 2026-09-21 |

### M-suite blocker note (2026-09-21)
M2–M12 require **driving the Mastodon UI** as `imuser` (compose, favorite, boost, reply, edit, delete, unfollow). Because Mastodon registration is **invite-only** and the pre-seeded `imuser`'s password is unknown, the agent cannot perform the Mastodon-side actions. **Options to unblock:** (a) operator provides the pre-seeded `imuser` password (or creates an invite + account the agent can use); (b) operator flips `registrations` to open so the agent can create `imuser`; (c) re-provision the Mastodon container with `REGISTRATION_MODE=open`. Until then, the **Iris→Mastodon outbound** leg (Iris following `imuser`, Iris posting to a followed Mastodon user) is testable **wire-side**, but any assertion that requires reading the **Mastodon UI** (M2.2, M4.2, M4.3, M5.x, M6.2, M7.x, M8.4, M9.x, M10.x, M11.x, M12.x) is **BLOCKED**. See the cross-instance **discovery** finding below for the Iris-side defect already reproduced.
