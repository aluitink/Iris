# 054 — Domain folders with nested namespaces, absorbed by per-project global usings

> Resolved 2026-08-29. Introduced by the Phase -1 reorganization ([change 068](../changes/068-phase1-domain-folder-reorganization.md)).

## Context

Every `src` project (and its test project) was a flat directory: all `.cs` files at the project root, all in a single namespace (`Iris.Core`, `Iris.Server`, …). As the surface grew (Iris.Core ~40 types, Iris.Server ~90, Iris.Client ~35) the flat layout made it hard to find a type by domain, and the single-namespace design meant every type in a project was visible to every other file — a latent source of name collisions.

The reorganization had to move code into domain folders **without changing behavior** and without triggering a mass `using` edit across the ~210 files that consume these projects.

## Decision

Folders mirror namespaces. Each domain folder becomes a nested namespace, and the flat root keeps only the project's "public composition" surface (options, DI extensions, constants, the main entry types).

- `Iris.Core` → `Identity/`, `Signing/`, `Caching/`, `Collections/` (root: `ActivityJson`, `Iris`).
- `Iris.Client` → `Discovery/`, `Collections/`, `Caching/`, `Pipeline/`, `Auth/` (root: client/factory/options/IRIs).
- `Iris.Server` → `Stores/`, `Inbox/`, `Delivery/`, `Security/`, `Services/`, `Caching/`, `Http/Proxy/` (root: DI extensions, options, constants, IRI helpers).
- `Iris.Server.InMemory` → `Stores/` (root: persistence provider + extensions).
- `Iris.Client.Extensions` → `Keys/`, `Sessions/` (root: factory/options/DI).

Test projects mirror the same folders and gain matching nested namespaces; root-level integration/endpoint/conformance/smoke and fixture files stay at the project root.

**Using-churn is absorbed, not hand-edited.** Because the SDK-style projects glob `**/*.cs` (no `.csproj` change is needed to move a file), the only mechanical cost is namespaces + `using`s. Rather than edit ~210 consuming files, each project gets a `GlobalUsings.cs` listing its own sub-namespaces, and every consuming project's `GlobalUsings.cs` is extended with the sub-namespace lines it needs. The pre-existing `using Iris.Core;` / `using Iris.Server;` keeps resolving, and the new sub-namespace types are reachable without per-file edits.

**Namespace/folder collision rule.** A folder/namespace must not share a name with an existing public type. The `Signatures` static class collided with a `Signatures/` folder, so that folder/namespace was renamed `Signing` (the class keeps its name). `Caching`, `Collections`, `Identity`, `Keys`, `Sessions` had no such collision.

**Cross-project type-name ambiguity.** When two sub-namespaces are global usings and both define a same-named type (e.g. `WebFingerCache` in `Iris.Server.Caching` and `Iris.Client.Discovery`; `CollectionPageCache` in `Iris.Server.Caching` and `Iris.Client.Collections`), the ambiguous file resolves it with a targeted `using` alias (or a global alias in the test project's `GlobalUsings.cs`) pointing at the intended type.

**Project-name vs namespace.** `Iris.Client.Extensions` is a *project* name, so `using Iris.Client.Extensions.Keys;` does not parse as a namespace import. Consuming files therefore reference the moved types with `using` aliases of the full type path (e.g. `using IrisSession = Iris.Client.Extensions.Sessions.IrisSession;`).

## Alternatives considered

### 1. Flat files, no folders

Rejected: the original state. At the current size the single-namespace flat layout is hard to navigate and invites name collisions as more types land.

### 2. Nested namespaces but hand-edit every consuming `using`

Rejected: correct but a ~210-file mechanical change that is error-prone and would bloat the diff, defeating the "low-risk, no behavior change" goal. Global usings achieve the same visibility with one file per project.

### 3. One folder per project, no nested namespaces

Rejected: folders help navigation but without nested namespaces the type's declared namespace would disagree with its folder, which is the exact drift this phase is meant to remove.

## Consequences

- A type's namespace now matches its folder, and related types are co-located by domain.
- New cross-project visibility is expressed in `GlobalUsings.cs` files rather than scattered `using` lines; adding a sub-namespace to a consumer is a one-line change.
- The collision rule and the alias-for-ambiguity pattern must be followed for any future folder added to these projects.
- `Iris.Client.Extensions` types are referenced by alias at the call site because of the project-name/namespace overlap — a one-time wart, not a recurring one.
- No behavior change: full build 0 warnings/0 errors and all 850 tests pass before and after.
