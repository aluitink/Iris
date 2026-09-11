// WebCrypto signing bridge for the Iris Blazor WASM explorer.
//
// The .NET-on-WASM BCL has no usable RSA implementation (RSA.Create() + ImportFromPem throws
// ArgumentException "Arg_PlatformNotSupported"), so the client cannot sign ActivityPub HTTP
// signatures in the browser the way it does on a server. The browser's WebCrypto
// (crypto.subtle) is fully capable of RSASSA-PKCS1-v1_5 + SHA-256 signing — the exact primitive
// the Iris signer uses (BCL rsa.SignData(data, SHA256, Pkcs1)) — so this module performs the
// crypto in JS and the C# WebCryptoSigningKey drives it via IJSRuntime.
//
// Imported keys are stored in a page-lifetime registry (JsRuntimeValue instances cannot be
// returned across the .NET/JS boundary), addressed by an opaque numeric id the C# side keeps.

// Page-lifetime registry of imported CryptoKey handles, addressed by id.
const keyRegistry = new Map();
let nextKeyId = 1;

// Decode a PEM body (the base64 between the BEGIN/END lines) into a Uint8Array of DER bytes.
function pemToDer(pem) {
    const lines = pem.split(/\r?\n/);
    const body = lines
        .filter((l) => !l.startsWith("-----") && l.trim().length > 0)
        .join("");
    const base64 = body.replace(/\s+/g, "");
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

// DER bytes -> PEM with the given label, 64-column wrapped.
function derToPem(der, label) {
    let base64 = "";
    for (let i = 0; i < der.length; i++) {
        base64 += String.fromCharCode(der[i]);
    }
    const b64 = btoa(base64);
    const wrapped = b64.match(/.{1,64}/g).join("\n");
    return `-----BEGIN ${label}-----\n${wrapped}\n-----END ${label}-----\n`;
}

// Encode a Uint8Array as a base64 string (for returning byte buffers to .NET).
function bytesToBase64(bytes) {
    let binary = "";
    const arr = new Uint8Array(bytes);
    for (let i = 0; i < arr.length; i++) {
        binary += String.fromCharCode(arr[i]);
    }
    return btoa(binary);
}

// Import a PKCS#8 RSA private key (the actor document's owner-only `privateKey` property) for
// signing. Returns the registry id for the imported CryptoKey.
async function importPrivateKey(pem) {
    const der = pemToDer(pem);
    const key = await crypto.subtle.importKey(
        "pkcs8",
        der.buffer.slice(der.byteOffset, der.byteOffset + der.byteLength),
        { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
        true, // extractable (so we can re-export the public key / JWK)
        ["sign"],
    );
    const id = nextKeyId++;
    keyRegistry.set(id, key);
    return id;
}

// Import a SubjectPublicKeyInfo (SPKI) RSA public key (the actor document's `publicKeyPem`) for
// verification / public-key export. Returns the registry id.
async function importPublicKey(pem) {
    const der = pemToDer(pem);
    const key = await crypto.subtle.importKey(
        "spki",
        der.buffer.slice(der.byteOffset, der.byteOffset + der.byteLength),
        { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
        true,
        ["verify"],
    );
    const id = nextKeyId++;
    keyRegistry.set(id, key);
    return id;
}

// Sign raw bytes with the registered private key (RSASSA-PKCS1-v1_5 + SHA-256). The browser
// hashes the input internally, matching BCL rsa.SignData(data, SHA256, Pkcs1). Returns the
// signature as base64.
async function sign(keyId, dataBase64) {
    const key = keyRegistry.get(keyId);
    if (!key) {
        throw new Error(`No WebCrypto key registered for id ${keyId}.`);
    }
    const binary = atob(dataBase64);
    const data = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        data[i] = binary.charCodeAt(i);
    }
    const signature = await crypto.subtle.sign(
        { name: "RSASSA-PKCS1-v1_5" },
        key,
        data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength),
    );
    return bytesToBase64(signature);
}

// Verify raw bytes against a signature with the registered public key. Returns a boolean.
async function verify(keyId, dataBase64, signatureBase64) {
    const key = keyRegistry.get(keyId);
    if (!key) {
        return false;
    }
    const toBytes = (b64) => {
        const binary = atob(b64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        return bytes;
    };
    const data = toBytes(dataBase64);
    const signature = toBytes(signatureBase64);
    try {
        return await crypto.subtle.verify(
            { name: "RSASSA-PKCS1-v1_5" },
            key,
            signature.buffer.slice(signature.byteOffset, signature.byteOffset + signature.byteLength),
            data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength),
        );
    } catch {
        return false;
    }
}

// Export the registered key's public part as SPKI PEM (the actor document's `publicKeyPem` form).
async function exportPublicKeyPem(keyId) {
    const key = keyRegistry.get(keyId);
    if (!key) {
        throw new Error(`No WebCrypto key registered for id ${keyId}.`);
    }
    const publicKey = await crypto.subtle.exportKey("spki", key);
    return derToPem(new Uint8Array(publicKey), "PUBLIC KEY");
}

// Export the registered private key's public part as a JWK (the actor document's `publicKey` form).
async function exportPublicJwk(keyId) {
    const key = keyRegistry.get(keyId);
    if (!key) {
        throw new Error(`No WebCrypto key registered for id ${keyId}.`);
    }
    const jwk = await crypto.subtle.exportKey("jwk", key);
    return JSON.stringify(jwk);
}

// Free a registered key (called on log-out / dispose). No-op if the id is unknown.
function free(keyId) {
    keyRegistry.delete(keyId);
}

// Expose the surface as a plain global namespace (window.webcryptoSign) so the C#
// WebCryptoSigningKey invokes it via IJSRuntime.InvokeAsync("webcryptoSign.<fn>", ...). A plain
// <script src> (not an ES module) is loaded in index.html before the Blazor scripts, which is the
// simplest supported interop pattern (no module-graph resolution required in the browser).
window.webcryptoSign = {
    importPrivateKey,
    importPublicKey,
    sign,
    verify,
    exportPublicKeyPem,
    exportPublicJwk,
    free,
};

// Bootstrap entry point for the Iris.WebCrypto library. Blazor's IJSRuntime can only call *named*
// global functions (it rejects inline JS string expressions), so the C# side calls this by name:
// webcryptoSignBootstrap.install(source). The host includes the bridge once via a single
// <script src=".../WebCrypto.js"> (see the package README); that script defines BOTH the signing
// surface (window.webcryptoSign, used for the actual crypto) AND this named install() stub. install()
// is provided for hosts that load the bridge lazily / re-inject after a navigation: it appends an
// inline <script> carrying `source` (the full bridge source the C# side embeds) and returns whether
// window.webcryptoSign is now defined. It is idempotent — if the bridge is already present it simply
// returns true. (A <script> with .text executes synchronously on insertion, so webcryptoSign is
// defined by the time install() returns.)
window.webcryptoSignBootstrap = {
    install(source) {
        if (typeof window.webcryptoSign !== "undefined") {
            return true;
        }
        const s = document.createElement("script");
        s.text = source;
        document.head.appendChild(s);
        return typeof window.webcryptoSign !== "undefined";
    },
};
