# Deploying GeoFoundry to the Web

## Requirements

Multithreaded Burst on the Web (`SharedArrayBuffer`) requires the page to be **cross-origin isolated**. The server that serves the build's `index.html` must send:

```
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
Cross-Origin-Resource-Policy: cross-origin
```

Without these headers Burst jobs silently fall back to the main thread (everything still works, but without parallelism).

## Local development

`File → Build And Run` in Unity 6.4 — the built-in development server **sets the required headers itself**. No extra setup is needed: use the **Default** WebGL Template and enable threading (`Player Settings → Web → Native C/C++ Multithreading: ON`).

To check that cross-origin isolation is active, run in the browser console:

```js
window.crossOriginIsolated
// → true
```

## Production servers

### Nginx

```nginx
location /geofoundry/ {
    add_header Cross-Origin-Opener-Policy "same-origin";
    add_header Cross-Origin-Embedder-Policy "require-corp";
    add_header Cross-Origin-Resource-Policy "cross-origin";
    try_files $uri $uri/ =404;
}
```

### Apache (`.htaccess`)

```apache
<IfModule mod_headers.c>
    Header set Cross-Origin-Opener-Policy "same-origin"
    Header set Cross-Origin-Embedder-Policy "require-corp"
    Header set Cross-Origin-Resource-Policy "cross-origin"
</IfModule>
```

### Caddy

```caddy
example.com {
    root * /var/www/geofoundry
    file_server
    header {
        Cross-Origin-Opener-Policy "same-origin"
        Cross-Origin-Embedder-Policy "require-corp"
        Cross-Origin-Resource-Policy "cross-origin"
    }
}
```

### Cloudflare Pages / Workers (`_headers`)

```
/*
  Cross-Origin-Opener-Policy: same-origin
  Cross-Origin-Embedder-Policy: require-corp
  Cross-Origin-Resource-Policy: cross-origin
```

### Vercel (`vercel.json`)

```json
{
  "headers": [
    {
      "source": "/(.*)",
      "headers": [
        { "key": "Cross-Origin-Opener-Policy", "value": "same-origin" },
        { "key": "Cross-Origin-Embedder-Policy", "value": "require-corp" },
        { "key": "Cross-Origin-Resource-Policy", "value": "cross-origin" }
      ]
    }
  ]
}
```

## Hosting without custom headers (GitHub Pages, etc.)

If you cannot set custom HTTP headers, the only workaround is [coi-serviceworker](https://github.com/gzuidhof/coi-serviceworker): it injects a script that registers a service worker, which adds COOP/COEP to every response via the `fetch` event. **Warning:** with Unity 6.4, worker pool crashes have been observed when coi-serviceworker runs alongside Unity's pthread workers, so this option is **not recommended** for production. Prefer a host that lets you configure headers (Cloudflare Pages, Vercel, Netlify, your own VPS).

## Brotli compression in production

In `Player Settings → Publishing Settings → Compression Format`, choose **Brotli** for the smallest size or **Gzip** for compatibility. The production server **must** send:

- `Content-Encoding: br` for `.br` files
- `Content-Encoding: gzip` for `.gz` files

Otherwise the Unity loader sees raw compressed bytes and reports `Failed to parse binary data file`. Most production servers do this automatically based on MIME mapping; on nginx/Caddy, set it explicitly:

```nginx
location ~ \.br$ {
    add_header Content-Encoding br;
    add_header Vary Accept-Encoding;
    types { } default_type application/octet-stream;
}
```

## Scheme (HTTPS / HTTP)

Browsers block **downgrades** (an HTTPS page loading an HTTP resource — Mixed Content) and do not grant `crossOriginIsolated` to HTTP pages outside localhost. Valid configurations:

| Build | Tile servers | Behavior |
|---|---|---|
| HTTPS | HTTPS | ✅ production configuration |
| HTTP (localhost) | HTTP (localhost) | ✅ fully local development |
| **HTTP (localhost)** | **HTTPS** | ✅ convenient for development: test the production tile setup with a local build without HTTPS (localhost is a secure context, so SharedArrayBuffer works; HTTP → HTTPS is an upgrade and is not blocked) |
| HTTPS | HTTP | ❌ Mixed Content — the browser blocks the HTTP requests; the Console shows `Mixed Content: ... was loaded over HTTPS, but requested an insecure resource` |
| HTTP (not localhost) | any | ❌ no `crossOriginIsolated`, SharedArrayBuffer is unavailable, multithreaded Burst is disabled |

In production, everything should be served over HTTPS. Tile servers (martin, titiler) must send `Access-Control-Allow-Origin` and `Cross-Origin-Resource-Policy: cross-origin` (or be proxied through the same domain as the build — then they are same-origin and these headers are not needed).

> **Self-signed HTTPS on intranet tile servers:** if the certificate is not trusted by the system, the browser blocks fetches with `ERR_CERT_AUTHORITY_INVALID`. Fix it with a trusted certificate (Let's Encrypt / corporate CA), or by manually accepting the certificate in the browser before launching the build.

## Touch input in mobile browsers

`MapCameraRig` (the optional camera module `GeoFoundry.CameraControl`, requires the Input System package) handles pinch/pan/rotate through the Input System. By default the browser intercepts these gestures on the canvas (page pinch-to-zoom, pull-to-refresh, scrolling) before they reach the game. To disable this, set the following in the WebGL template's HTML (`index.html`, on `#unity-canvas` or its container):

```css
#unity-canvas {
    touch-action: none;
}
```

Unity's **Default** template does not do this. If you use it (see [Local development](#local-development) above), either override it with a copy that includes this rule (`Assets/WebGLTemplates/...`), or add `touch-action: none` in your own custom template when deploying to production with touch controls.

## Memory size

Unity Web builds are limited to ~4 GB of WASM heap. In `Player Settings → Web → Memory Size`:
- `Initial Memory Size` ≈ 256 MB (close to typical usage),
- `Maximum Memory Size` ≈ 2032 MB,
- `Memory Growth Mode` = Geometric (default).

If you hit out-of-memory crashes, lower the tile memory budgets in `GeoFoundryConfig` (`Gpu Budget MB`, `Heap Budget MB`).
