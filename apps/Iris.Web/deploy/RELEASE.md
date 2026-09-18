# Iris — Release Checklist

Step-by-step procedure for cutting a release of Iris.Web.

## Prerequisites

- All slices for the release are complete and committed.
- `dotnet build -c Release` passes with 0 errors.
- `dotnet test` (full suite) passes with 0 failures.
- `CHANGELOG.md` is updated with the new version.
- `Directory.Build.props` has the correct `<Version>` value.

## 1. Tag the release

```bash
cd /workspace
# Verify the version in Directory.Build.props matches the tag you're about to create:
grep '<Version>' Directory.Build.props
# Example: 1.0.0 → tag v1.0.0
git tag -a v1.0.0 -m "Iris v1.0.0 — first production release"
git push origin v1.0.0
```

## 2. Build the release artifacts

```bash
# Build + test (full suite, including slow tests):
dotnet build -c Release
dotnet test -c Release

# Build the Docker image:
cd apps/Iris.Web
docker compose build --no-cache iris-web
```

## 3. Push to a container registry

For a private registry (e.g., GHCR, ECR, Harbor):

```bash
# Tag the image:
docker tag iris-web-iris-web:latest <registry>/iris/iris-web:v1.0.0

# Push:
docker push <registry>/iris/iris-web:v1.0.0
```

For a self-hosted deployment (no registry), skip this step — the compose stack
builds the image locally from source.

## 4. Deploy

### Self-hosted (docker compose, build from source)

```bash
# On the production host:
git pull origin main          # or the release branch
git checkout v1.0.0

cd apps/Iris.Web
# .env must be in place (see .env.example for required variables)
docker compose up -d --build

# Verify:
curl -s http://localhost:8088/ap/v1/health | jq
curl -s http://localhost:8088/ap/v1/ready | jq
```

### Registry-based (pull pre-built image)

Edit `docker-compose.yml` to use the registry image instead of building:

```yaml
  iris-web:
    image: <registry>/iris/iris-web:v1.0.0
    # remove the build: section
```

Then:

```bash
docker compose pull iris-web
docker compose up -d
```

## 5. Verify in production

### Health + readiness

```bash
curl -s https://iris.luit.ink/ap/v1/health | jq
curl -s https://iris.luit.ink/ap/v1/ready | jq
```

### WebFinger (identity resolution)

```bash
curl -s "https://iris.luit.ink/.well-known/webfinger?resource=acct:alice,iris.luit.ink" | jq
```

### NodeInfo (federation metadata)

```bash
curl -s https://iris.luit.ink/nodeinfo/2.0 | jq
```

### Actor document

```bash
curl -s https://iris.luit.ink/ap/v1/u/alice | jq .id
```

### UI (browser)

1. Navigate to `https://iris.luit.ink` in a browser.
2. Sign in with the seeded admin (`alice` / `alice-password`).
3. Verify the home timeline renders.
4. Post a test note via `/compose`.
5. Verify the note appears on `/profile` and in the home timeline.
6. Check DevTools → Console for errors (should be 0).
7. Check DevTools → Network → `/_blazor` request (should be 101 WebSocket).

### Metrics endpoint

```bash
curl -s http://localhost:8088/local/v1/metrics | head -20
```

### API documentation

```bash
curl -s https://iris.luit.ink/openapi/v1.json | jq '.info.version'
# Swagger UI:
# https://iris.luit.ink/api/
```

## 6. Post-release

- Update `CHANGELOG.md` with the release date (if not already set).
- Push the CHANGELOG update.
- Notify stakeholders (Slack, email, etc.).
- Monitor `/ap/v1/health` + `/local/v1/metrics` for 30 minutes post-deploy.

## Rollback

If the release is broken:

```bash
# Self-hosted:
git checkout <previous-tag>
cd apps/Iris.Web
docker compose up -d --build

# Registry-based:
# Revert the image tag in docker-compose.yml to the previous version
# docker compose pull iris-web && docker compose up -d
```

Data in the PostgreSQL volume is preserved across rollbacks (the app's EF
migrations are forward-only; a rollback to a prior version with the same schema
is safe as long as no breaking migration was applied).
