# Custom images for the Lemmy interop instance (Phase 135.1).
#
# WHY custom images:
#  1. This host's Docker runtime **bind mounts are broken** (a `-v file:target` creates an empty
#     directory at the target instead of binding the file — verified with trivial cases), so both
#     the UI patch and the nginx config are BAKED into images via COPY rather than bind-mounted.
#  2. The stock lemmy-ui 0.19.20 SSR has a bug that 500s every page (site_res: undefined):
#
#       function X(e){return "http"+e+"://"+(J()?de():J()?Pe:process.env.LEMMY_UI_LEMMY_INTERNAL_HOST??Pe);var t}
#
#     The LEMMY_UI_LEMMY_INTERNAL_HOST env var is unconditionally prefixed with "http[s]://", so:
#      - with LEMMY_UI_HTTPS=true  it yields https://http://... (scheme in the host) -> 500
#      - with LEMMY_UI_HTTPS=false it yields http://http://...   (scheme in the host) -> 500
#     i.e. ANY usable internal host (which needs a scheme to disambiguate plain-HTTP from the
#     https-forcing me() helper) is rejected. We need LEMMY_UI_HTTPS=true (the browser-facing IRIs
#     must be https://lemmy.luit.ink), and the internal call is plain HTTP, so the only fix is
#     to stop the code from adding a second scheme.
#
# The patch makes X() return the env var VERBATIM when it already starts with "http", and
# otherwise keeps the original "http[s]://host:port" behavior. With
# LEMMY_UI_LEMMY_INTERNAL_HOST=http://lemmy-1:8536 (container name on the shared network, plain
# HTTP) SSR now calls the backend correctly.
#
# Build targets:
#   --target ui     : patched lemmy-ui (docker-compose service `ui`)
#   --target proxy   : nginx front with the single-port routing config (service `proxy`)

# ---------------------------------------------------------------------------
# ui: patched lemmy-ui
# ---------------------------------------------------------------------------
FROM dessalines/lemmy-ui:0.19.20 AS ui

# Baked because this host's Docker runtime bind mounts are broken (see above).
# The image runs as the unprivileged `node` user, so it can't delete root-owned files; the script
# is harmless to leave in /tmp.
COPY patch-lemmy-ui.js /tmp/patch-lemmy-ui.js
RUN node /tmp/patch-lemmy-ui.js

# ---------------------------------------------------------------------------
# proxy: nginx front (single published port: host 8091 -> container 8082)
# ---------------------------------------------------------------------------
FROM nginx:1.27-alpine AS proxy

# Baked config (this host's Docker runtime bind mounts are broken — see above).
COPY nginx.conf /etc/nginx/conf.d/default.conf
