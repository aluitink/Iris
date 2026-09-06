# Production App — Media Storage Design

> **Level 3.** Parent: [production-app-persistence.md](production-app-persistence.md). Grandparent: [production-app-overview.md](production-app-overview.md).

## 1. Split: metadata vs. bytes

`IMediaStore` (the existing `Iris.Server` interface) is implemented by `Iris.Server.Data`'s media store, which itself splits into two concerns:

1. **Metadata** — a normal `MediaAsset` row in PostgreSQL (see [production-app-persistence-schema.md](production-app-persistence-schema.md) §3): id, content-type, file name, size, hash, owner, created-at, and *which backend + key* holds the bytes.
2. **Bytes** — behind a new, small abstraction that the media store composes:

```csharp
public interface IMediaBlobStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream?> GetAsync(string key, CancellationToken ct = default); // null = not found
    Task DeleteAsync(string key, CancellationToken ct = default);
}
```

`key` is an opaque storage-backend key (for local disk: a relative file path; for S3: an object key) — not necessarily the same as the media id, though using the media id as the key is the simplest default.

## 2. Backend options

| Backend | When to use | Pros | Cons |
|---|---|---|---|
| **Local disk** *(MVP default)* | Single-instance deployment, the common self-hosted case | Zero extra services in Compose; trivial to implement (`File.Create`/`File.OpenRead` under a configured root, mirroring `FileBackedMediaStore`'s existing sibling-file approach); backed by a plain named Docker volume | Doesn't scale past one host's disk; no built-in CDN/offload story; backing up media means backing up a volume, not just a DB dump |
| **S3-compatible (MinIO self-hosted, or AWS S3)** | Once storage needs to scale independently of the app host, or media should be served via a CDN/offloaded from the app container | Scales independently; MinIO is self-hostable (stays in the same Compose stack, no cloud dependency required); AWS S3 (or R2/Backblaze B2, etc.) works unchanged since MinIO speaks the S3 API — same client code, different endpoint/credentials | An extra moving part (a service + credentials) that the MVP doesn't strictly need on day one |

**Recommendation:** ship the **local disk** backend for the MVP (matches the "broad MVP first" principle — don't stand up MinIO before there's a reason to), but build `IMediaBlobStorage` as the seam from day one so adding the S3 backend later is additive (`services.AddSingleton<IMediaBlobStorage, S3MediaBlobStorage>()` behind a config switch — e.g. `Media:Backend: Local|S3` — instead of `LocalMediaBlobStorage`). Use `AWSSDK.S3` (works against both real AWS S3 and any S3-compatible endpoint including MinIO, by overriding the service URL) rather than a MinIO-specific SDK, so the same code serves both self-hosted and cloud deployments.

## 3. Serving media back out

No change to the existing routes: `GET /ap/v1/media/{id}` resolves the `MediaAsset` row (content-type, storage key), then streams from whichever `IMediaBlobStorage` is configured. `GET /ap/v1/media/proxy?url=…` (fetch-and-cache a remote attachment) also stores through the same abstraction — remote media a local user's client renders gets pulled through `IMediaFetcher` (unchanged) and persisted via `IMediaBlobStorage` exactly like a local upload.

## 4. Validation & limits

Unchanged: reuse the existing 10 MiB upload cap and content-type/dedup-by-hash behavior already implemented in `FileBackedMediaStore` — the new store's `PutAsync` should compute the same content hash and dedupe before delegating to `IMediaBlobStorage.PutAsync`.

## 4.1 Dedup ownership & deletion (a gap in the inherited design, worth closing here)

`FileBackedMediaStore`'s dedup-by-hash behavior was built for a single-owner sample; a multi-tenant production app needs an explicit answer to "what happens when two different actors upload byte-identical content and one of them deletes it?" — otherwise a delete silently breaks another actor's still-visible post:

- **Dedup the blob, not the `MediaAsset` row.** Each upload still gets its own `MediaAsset` metadata row (its own id, owner, created-at) even when the bytes are identical to an existing upload — only the underlying `IMediaBlobStorage` key is shared (resolved by content hash). This means ownership and deletion are always per-row, never shared.
- **Reference-count the blob key.** A `MediaAsset` row references a blob by `StorageKey` (already in the entity sketch, [production-app-persistence-schema.md](production-app-persistence-schema.md) §3); before `IMediaBlobStorage.DeleteAsync` actually removes the bytes, check whether any other `MediaAsset` row still references that `StorageKey` (a simple count query) — only delete the blob when the count drops to zero. Deleting the `MediaAsset` row itself (the user's own reference) always succeeds regardless of the shared-blob refcount.
- This is a small addition to the media store's delete path, not a new abstraction — call it out explicitly when implementing `IMediaStore.DeleteAsync` (or equivalent) in `Iris.Server.Data` so it isn't accidentally built as a naive "delete the row and the blob" that clobbers a second uploader.

## 5. Phase 2/3 ideas (not MVP)

- **Image variants/thumbnails** (a small + a full-size render) for feed performance — generate on upload (e.g., via `SixLabors.ImageSharp`) and store both, with the metadata row tracking variant keys. This is an "experience/polish" pass item ([production-app-feature-set.md](production-app-feature-set.md) Phase C/D), not a functionality-pass requirement.
- **CDN-friendly cache headers** on the S3 backend (long `max-age` + content-hash-based keys are already effectively immutable, matching the existing `Cache-Control: max-age=31536000, immutable` the route sets today).

## 6. What "done" looks like

- `IMediaBlobStorage` exists with a local-disk implementation wired as the default.
- Upload → serve round-trips correctly through PostgreSQL metadata + disk bytes.
- The abstraction has at least one test proving it's swappable (e.g., an in-memory `IMediaBlobStorage` test double used in a unit test of the media store's dedup logic, independent of the real backend).
