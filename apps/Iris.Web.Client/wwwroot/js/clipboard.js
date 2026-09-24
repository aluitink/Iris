// Clipboard bridge for the WASM client. Defines window.copyText(text) -> Promise<boolean>,
// which copies `text` to the clipboard via the async Clipboard API and reports success.
// A failure (permission denied, non-secure context, unsupported browser) resolves false rather
// than rejecting, so the caller can fall back to showing the link without surfacing an error.
window.copyText = async function (text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch (_e) {
        return false;
    }
};
