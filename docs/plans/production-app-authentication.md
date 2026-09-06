# Production App — Authentication

> **Level 2.** Parent: [production-app-overview.md](production-app-overview.md). Child: [production-app-auth-flows.md](production-app-auth-flows.md).

## 1. Scope for the MVP

**Local username/password registration and login only.** This is the bootstrap mechanism — it gets a working, self-contained product without depending on any external identity provider. Two things it must produce:

1. A **local user account** (username, password hash, role) that can authenticate a browser session for `Iris.Web`.
2. A **local ActivityPub actor** (a `Person`, with a real key pair) linked 1:1 to that account — because the whole point is that the account *is* a federated identity, not a separate concept bolted next to one.

> **Where this sits relative to the "no new APIs" rule** ([production-app-overview.md](production-app-overview.md) §3): registration/login is the **one** deliberate exception, because ActivityPub itself has no concept of a browser-session login — there is nothing to be "AP-native" about here. Everything *before* a session exists (the `/register` and `/login` forms) is local, plain ASP.NET Core auth. Everything *after* — every post, follow, like, moderation action, settings change, media upload — goes through `IActivityPubClient` exactly like any other authenticated action in this app (see [production-app-web-host.md](production-app-web-host.md) §3). Local actor *provisioning* during registration also isn't a new API: it calls the library's existing host-side actor-management surface in-process (the same mechanism `SampleServer`'s seed logic already uses to create `alice`/`bob`/the sample community) — not a wire endpoint, and not something this plan invents.

Everything else — OAuth2 as the actual token mechanism, external IdP login (Google, etc.) — is explicitly **out of scope for the MVP** and sequenced as later phases below. Do not build them now; do design the account/actor link so they slot in without a rewrite (see §4).

## 2. Registration flow (high level)

1. User submits username + password (+ optional display name) on `/register`.
2. Validate: username uniqueness, password strength (a reasonable minimum — length, not a gauntlet), username format constraints that also satisfy the actor handle rules the library already enforces.
3. Hash the password (see [production-app-auth-flows.md](production-app-auth-flows.md) for the concrete algorithm — do not roll custom crypto).
4. Provision a local actor: generate a `KeyPair` (reuse `Iris.Core`'s `KeyPairGenerator`), create the `Person` actor document via the existing actor-management path the server already uses to create local actors (mirror however `SampleServer`'s seed logic does it), handle = username.
5. Persist the `UserAccount` (username, password hash, role, linked actor IRI, created-at) and the actor (via the normal `IActorStore`/`IKeyStore` — nothing new here, this is just "create an actor" through the existing path).
6. Sign the user in (issue the auth cookie) and redirect to the app.

## 3. Login flow (high level)

1. User submits username + password on `/login`.
2. Look up the account, verify the password hash.
3. On success, sign in via ASP.NET Core cookie authentication — claims include the user id, username, linked actor IRI, and role.
4. `IActorSessionAccessor` (see [production-app-web-host.md](production-app-web-host.md) §3) uses those claims for the rest of the circuit's lifetime.
5. On failure, generic "invalid username or password" (don't leak which field was wrong); rate-limit repeated failures per username/IP (reuse the shape of the existing `IInboundRateLimiter`/sliding-window pattern already in `Iris.Server.Security` as a model, even though this is a different call site).

## 4. Why this design leaves room for OAuth2 and external login

- The account/actor link (`UserAccount.ActorIri`) is the durable identity concept — whatever *authenticates* the browser (cookie today, OAuth2 bearer tomorrow, a Google-issued external claim later) just needs to resolve to that same link.
- `Iris.Server` **already has OAuth2 endpoints** (`/ap/v1/oauth2/authorize`, `/ap/v1/oauth2/token`, `/ap/v1/oauth2/revoke`) and `IActorCredentialValidator` implementations (`BasicAuthCredentialValidator`, `BearerTokenCredentialValidator`) — per the user's own note, these are implemented but "never actually tested." A natural **Phase 2** for this workstream (after the MVP registration/login above ships) is: write the integration tests that actually exercise the OAuth2 authorize→token→revoke round trip end-to-end, then switch `Iris.Web`'s own API calls (via `IActorSessionAccessor`) from the in-process key shortcut to a real OAuth2 bearer token obtained through that flow. This proves the library's own OAuth2 support with a real caller and gives `Iris.Web` a token-based auth story for any future non-Blazor client (a mobile app, a CLI) for free.
- **Phase 3**, external IdP login (Google/GitHub/etc. via `Microsoft.AspNetCore.Authentication.Google` and friends): add an external-login table (`ExternalLogin`: provider, provider-user-id, linked `UserAccount`) and an account-linking screen ("sign in with Google" either creates a new local account + actor, or links to an existing one if the user is already signed in). This is additive to the schema and the auth pipeline; it does not change how the actor/account link works.

## 5. Deliberately NOT using ASP.NET Core Identity (the full framework)

`Microsoft.AspNetCore.Identity`'s full framework (its own `DbContext`, migrations, `UserManager`/`SignInManager`, email confirmation, 2FA scaffolding, lockout policy tables, etc.) is more machinery than this MVP needs and brings its own opinionated schema/migration surface — friction the user explicitly wants to minimize. Instead:

- Reuse just `Microsoft.AspNetCore.Cryptography.KeyDerivation` (PBKDF2, the same algorithm ASP.NET Core Identity uses under the hood) or `Microsoft.AspNetCore.Identity`'s standalone `PasswordHasher<TUser>` class (it can be used without adopting the rest of Identity) for password hashing — don't hand-roll hashing.
- Write a small, explicit `IUserAccountStore` with exactly the methods this app needs (create, find by username, verify, update role). **Unlike the AP-native store interfaces** (`IActorStore` et al., which are declared in `Iris.Server` and implemented by a swappable persistence project), `IUserAccountStore` has no reason to be swappable independently of `Iris.Server.Data` — so it is declared *and* implemented in `Iris.Server.Data` itself (e.g. `Iris.Server.Data/Accounts/IUserAccountStore.cs`), not in `Iris.Server`. Declaring it in `Iris.Web` instead would create a circular project reference (`Iris.Server.Data` would need to reference `Iris.Web` for the interface, while `Iris.Web` already references `Iris.Server.Data` for `AddEntityFrameworkPersistence`) — keep it out of `Iris.Server` too, since that project has no concept of local user accounts (§1's whole point). `Iris.Web` only ever consumes `IUserAccountStore` through DI, never reimplements it. This keeps the "no persistence-technology leakage" rule intact and avoids importing a schema the app doesn't fully use.
- Use plain ASP.NET Core cookie authentication (`AddAuthentication().AddCookie(...)`) for the browser session — no need for Identity's `SignInManager` ceremony to get a working cookie.

## 6. Roles & admin bootstrap

- Two roles are enough for the MVP: `User` and `Admin`. `Admin` gates the instance-admin UI (moderation queue across all users, instance settings) in [production-app-feature-set.md](production-app-feature-set.md).
- Bootstrap the first admin via an environment variable (`IRIS_ADMIN_USERNAME` / `IRIS_ADMIN_PASSWORD` at the `.env`/Compose layer, bound to `App:Admin:Username` / `App:Admin:Password` in configuration — not `Iris:Admin:*`, since `Iris:*` is reserved for `AddActivityPubServer`'s bound `ActivityPubServerOptions`/delivery/observability options (Phase 30.1 convention) and admin bootstrap is an `Iris.Web`-only concern, see [production-app-deployment.md](production-app-deployment.md)): on first startup, if no `Admin` account exists and both variables are set, create one automatically. This avoids a chicken-and-egg "how do I get my first admin" problem without building a separate bootstrap CLI.

## 7. Account recovery (deliberately minimal in the MVP)

**No email address is collected at registration** (§2 — username + password + display name only), so there is **no self-service "forgot password" flow in the MVP**: nothing to send a reset link to. This is a deliberate scope cut, not an oversight, but a locked-out user needs *some* recovery path other than losing the account (and its linked ActivityPub identity) forever:

- **MVP recovery path: admin-assisted reset.** An `Admin`-only action (instance admin screen, [production-app-feature-set.md](production-app-feature-set.md)) that sets a new password hash for a given username — no email, no token, just an authenticated admin action against `IUserAccountStore.UpdatePasswordHashAsync`, the same method the user's own "change password" flow already uses. This requires trusting the instance admin (reasonable for a small/self-hosted instance) and is explicitly *not* a substitute for a real reset flow at scale.
- **A single-user or admin-less deployment has no recovery path at all** if the admin's own credentials are lost — document this plainly (in the deployment doc) rather than silently accepting the gap: the operator's only remaining option is a direct database update (`UPDATE "UserAccounts" SET "PasswordHash" = ...` with a `PasswordHasher`-produced hash) or re-running the `AdminBootstrapper` path after manually clearing the `Admin` role from the affected row (`AnyAdminExistsAsync` must return false for it to re-trigger).
- **Phase 2** (once email is worth collecting for other reasons — notifications-by-email, federation-adjacent verification, etc.): add an optional `Email` column + a token-based reset flow (a time-limited, single-use token stored alongside `UserAccount`, emailed via whatever SMTP/transactional-email service the deployment adds). Not built now; noted so the schema doesn't need to fight this later (an optional nullable `Email` column can be added additively whenever this graduates).

## 8. What "done" looks like for this workstream

- A new user can register, gets a working local ActivityPub actor with a real key pair, and is immediately signed in.
- A registered user can log out and log back in.
- Password hashes are never stored or logged in plaintext; failed logins are rate-limited.
- The first admin account bootstraps from `.env` on a clean deployment.
- An admin can reset another user's password (the MVP's only account-recovery path, §7).
- The account/actor link is modeled so OAuth2 and external-IdP login are additive, not a rewrite (documented above, not necessarily built yet).

See [production-app-auth-flows.md](production-app-auth-flows.md) for the concrete data model, hashing algorithm, and sequence diagrams.
