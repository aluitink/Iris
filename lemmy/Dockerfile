# Custom Lemmy image for the Iris <-> Lemmy interop instance (Phase 135.1).
#
# WHY a custom image: this host's Docker runtime **bind mounts are broken** (a `-v file:target`
# creates an empty directory at the target instead of binding the file — verified with trivial
# cases). The Iris containers avoid this by using named volumes / build contexts, not runtime file
# bind mounts. So we BAKE the hjson config into the image instead of bind-mounting it at runtime.
#
# The baked config lives at /config/config.hjson — Lemmy 0.19's default config location
# (relative `config/config.hjson` from WORKDIR `/`), so no LEMMY_CONFIG_LOCATION env var is needed.

FROM dessalines/lemmy:0.19.20

# COPY with the default (world-readable) perms is enough — the lemmy user can read it.
COPY config/config.hjson /config/config.hjson
