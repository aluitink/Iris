# 068 — Phase -1: domain folders + nested namespaces for all projects

> 2026-08-29 · Phase -1 (Project Reorganization)

## What was built

Reorganized every `src` project and its test project from a flat file layout into **domain folders with nested namespaces** (mirrored between src and tests). This is a pure structural cleanup — no type was renamed, no public API changed, and no behavior changed. The ~210-file `using` churn that the namespace nesting would normally cause is absorbed by `GlobalUsings.cs` files instead of per-file edits.

## Key types & files

| Area | Change |
|---|---|
| `Iris.Core` | `Identity/`, `Signing/`, `Caching/`, `Collections/`; root keeps `ActivityJson`, `Iris`. `Signatures` folder renamed `Signing` to avoid colliding with the `Signatures` static class. |
| `Iris.Client` | `Discovery/`, `Collections/`, `Caching/`, `Pipeline/`, `Auth/`; root keeps client/factory/options/IRI types. |
| `Iris.Server` | `Stores/`, `Inbox/`, `Delivery/`, `Security/`, `Services/`, `Caching/`, `Http/Proxy/`; root keeps DI extensions, options, constants, IRI helpers. |
| `Iris.Server.InMemory` | `Stores/`; root keeps persistence provider + extensions. |
| `Iris.Client.Extensions` | `Keys/`, `Sessions/`; root keeps factory/options/DI. |
| Test projects | Mirror the same folders with nested namespaces (`Iris.Core.Tests.Identity`, …); root-level integration/endpoint/conformance/smoke + fixture files stay at the project root. |
| `GlobalUsings.cs` | Added inside each reorganized project and extended in every consuming project (samples + all test projects) with the sub-namespace lines, so the existing `using <Project>;` keeps resolving. |
| Aliases | Targeted `using` aliases disambiguate the two cross-project same-named types (`WebFingerCache`, `CollectionPageCache`) and the `Iris.Client.Extensions` project-name/namespace overlap. |

No `.csproj` changes were needed (SDK-style `**/*.cs` glob). The four untracked root scratch files (`probe.csproj` + three emptied debug test files) were left in place, untracked, unchanged.

## Tests

850 before → 850 after. Full solution build 0 warnings/0 errors and all tests pass at both the entry and exit of the phase. No test was added, removed, or rewritten — only moved and re-namespaced.

## Decisions

- Domain folders with nested namespaces, absorbed by per-project global usings — see [Decision 054](../decisions/054-domain-folders-and-global-usings.md).
