// One-shot patch for the lemmy-ui 0.19.20 bundles (run at image build time).
//
// Both /app/dist/js/server.js (SSR) and /app/dist/js/client.js (browser) contain a minified
// helper that builds the Lemmy backend base URL:
//
//     function X(e){return void 0===e&&(e=""),"http"+e+"://"+(IS_BROWSER?de():IS_BROWSER?Pe:process.env.LEMMY_UI_LEMMY_INTERNAL_HOST??Pe);var t}
//
// (X/de/Pe and Ot/Zt/fn are the minified names in server.js and client.js respectively.)
//
// The env var value is unconditionally prefixed with "http[s]://", where e is "" or "s"
// (s when LEMMY_UI_HTTPS=true). This breaks in two ways:
//
//  1. SSR (server.js): we need LEMMY_UI_HTTPS=true so the browser-facing IRIs are https, which
//     makes e="s" and the internal call become https://http://lemmy-1:8536 (if the env var has a
//     scheme) or the wrong scheme for a plain-HTTP proxy. Every SSR page 500s (site_res: undefined).
//
//  2. Browser (client.js): the browser fetches its own API from this URL. With LEMMY_UI_HTTPS=true
//     it builds https://https://lemmy.luit.ink/... (doubled scheme) -> ERR_NAME_NOT_RESOLVED, which
//     the browser surfaces as a CORS/network failure on login and every authenticated call.
//
// FIX:
//  - server.js: if the env var already starts with "http", use it VERBATIM (we set it to
//    http://lemmy-1:8536, a plain-HTTP proxy on the shared Docker network). Otherwise keep the
//    original "http[s]://host:port" behavior.
//  - client.js: return "" (relative, same-origin). The browser page is served from the same origin
//    as the API (both go through the single-port nginx front), so relative /api/v3/* fetches hit
//    the backend via the proxy with no CORS and no scheme/host at all. This is robust regardless of
//    how the page is served (http or https, any FQDN).
//
// The patch is idempotent (skips if already applied) and fails loudly if the expected minified
// string is not found (i.e. the lemmy-ui version changed and the strings need updating).

const fs = require("fs");

function patchFile(file, oldStr, newStr) {
  const src = fs.readFileSync(file, "utf8");
  if (src.includes(newStr)) {
    console.log(file + ": already patched");
    return;
  }
  if (!src.includes(oldStr)) {
    console.error("FATAL: expected function not found in " + file);
    console.error("the lemmy-ui version may have changed; update the OLD string");
    process.exit(1);
  }
  const count = src.split(oldStr).length - 1;
  if (count !== 1) {
    console.error("FATAL: expected exactly 1 occurrence in " + file + ", found " + count);
    process.exit(1);
  }
  fs.writeFileSync(file, src.replace(oldStr, newStr));
  console.log("patched " + file);
}

// server.js: env var verbatim when scheme-bearing, else original behavior.
// (Tolerant of the original first iteration of this patch, which is functionally identical.)
const SERVER_NEW =
  'function X(e){return void 0===e&&(e=""),(t=process.env.LEMMY_UI_LEMMY_INTERNAL_HOST),t&&0===t.indexOf("http")?t:"http"+e+"://"+(J()?de():J()?Pe:null!=t?t:Pe);var t}';
const SERVER_OLD_ORIG =
  'function X(e){return void 0===e&&(e=""),"http"+e+"://"+(J()?de():J()?Pe:null!=(t=process.env.LEMMY_UI_LEMMY_INTERNAL_HOST)?t:Pe);var t}';
patchFile("/app/dist/js/server.js", SERVER_OLD_ORIG, SERVER_NEW);

// client.js: relative (same-origin) base URL — the browser page and the API share one origin.
patchFile(
  "/app/dist/js/client.js",
  'function Ot(e){return void 0===e&&(e=""),"http"+e+"://"+(Bt()?Zt():Bt()?fn:null!=(t=process.env.LEMMY_UI_LEMMY_INTERNAL_HOST)?t:fn);var t}',
  'function Ot(e){return ""}'
);

console.log("done");
