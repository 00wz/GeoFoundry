# Local MVT and DEM archives

`PmTilesVectorTileSource` and `PmTilesDemTileSource` implement the Core source
interfaces using one PMTiles v3 file per source. No SQLite, native plug-in,
whole-archive WebGL download or file-picker UI is required. The C# reader is in
`Runtime/TileIO/PmTiles`; concrete sources are in `Runtime/TileIO/Sources`.

## Configure a source

Place `moscow.pmtiles` and `moscow-dem.pmtiles` in `Assets/StreamingAssets/Maps`:

```csharp
using GeoFoundry.TileIO.PmTiles;
using GeoFoundry.TileIO.Sources;
using Unity.Collections;

var vectors = new PmTilesVectorTileSource(
    PmTilesByteSources.StreamingAsset("Maps/moscow.pmtiles"));
var dem = new PmTilesDemTileSource(
    PmTilesByteSources.StreamingAsset("Maps/moscow-dem.pmtiles"), tileSizePixels: 256);

// Called on the Unity main thread. Pass a cancellation token tied to your map/session.
using var tile = await vectors.GetTileAsync(coord, Allocator.Persistent, ct);
using var heights = await dem.GetHeightsAsync(coord, Allocator.Persistent, ct);
// A missing/empty DEM has heights.IsCreated == false; do not sample it.

// Keep sources alive across tile requests, then Dispose them when the map closes.
vectors.Dispose();
dem.Dispose();
```

These sources can be passed to consumers accepting `IVectorTileSource` and
`IDemTileSource`. The URL-only `GeoFoundryConfig`/bootstrapper is unchanged;
this API does not implicitly replace HTTP sources created by that bootstrapper.
Absolute filesystem paths and absolute HTTP(S) archive URLs are also accepted.
`StreamingAsset` escapes URL path segments on Android/Web. It rejects absolute
and parent-traversal names. Tile coordinates are XYZ, never manually TMS-flipped.
Invalid/out-of-world coordinates are missing tiles; X is not implicitly wrapped.
Missing vector tiles produce an empty byte array; missing/decoded-empty DEM
tiles produce default NativeArray without exceptions or logging.

The returned NativeArray belongs to the caller. Use Persistent/TempJob for
asynchronous work, dispose each created result. Sources/readers are IDisposable;
disposing cancels pending work. An injected reader is borrowed unless ownsReader
is true; a reader likewise borrows its byte source unless ownsSource is true.
Cancellation throws OperationCanceledException; external failures/corrupt archives
throw PmTilesException, a TileSourceException subclass.

## Platform behavior

| Platform | Archive access |
| --- | --- |
| Desktop / Editor | FileStream ranges; each read has its own file position |
| iOS | Filesystem ranges; not device-tested yet |
| Android filesystem | FileStream ranges |
| Android StreamingAssets inside APK | UWR + DownloadHandlerFile extracts once per source instance into persistentDataPath; then FileStream ranges |
| WebGL | HTTP(S) range reads only; the hosting server must satisfy the requirements below |

Android extraction uses a unique temporary archive, deleted at Dispose or failed
initialization/cancellation. It is not a persistent cross-session cache: no stale
cache invalidation problem, but a new source/session copies the archive again.
Allow free storage for the extracted archive and first-load extraction time.
No assumption about APK ZIP compression or direct seeking inside jar URLs is made.

## Web server requirements

The same `.pmtiles` can be hosted statically or served as a tile source by Martin.
When this reader accesses the archive URL directly, the server must:

- Honor a single `Range: bytes=start-end` with **206 Partial Content**.
- Return exact `Content-Range: bytes start-end/total` and the requested body length.
- Serve raw archive bytes: disable dynamic gzip/Brotli/content rewriting for
  `.pmtiles`. Internal gzip inside PMTiles is separate and supported.
- Keep the archive immutable for a source's lifetime. Publish updates at new URLs
  or close/reopen sources; length changes are rejected, same-size mutations are
  not guaranteed to be detected.
- For cross-origin archives, allow the build's origin and Range header through
  CORS, and expose Content-Range, Content-Length and Content-Encoding. Use HTTPS
  for HTTPS builds. Unity's HTTP transport outside Web also follows PlayerSettings
  insecure HTTP policy.
- For threaded Unity 6.4 builds, configure the page's COOP/COEP headers as in
  [web-deployment.md](web-deployment.md). The supplied development server includes them.

**Unity's default Build and Run SimpleWebServer is unsuitable for PMTiles** in
tested 6000.4.8f1: it advertises Accept-Ranges but returns 200 and the entire file.
Use Build, then serve that output with the included server instead of the default
Build and Run server. No file-picker workaround is provided.

On WebGL the byte source uses a small JS streaming-fetch plug-in. It rejects a
200 response at headers, before reading its body, and caps the actual returned
range. This avoids Unity's standard web loader reserving a buffer for the whole
archive before C# sees it. On other platforms HTTP ranges use UnityWebRequest.
There is deliberately no automatic full-file fallback, including for small files.

### Included development server

Requires Node.js 18 or newer, no npm packages. From the Unity project root:

```powershell
node Packages/com.00wz.geofoundry/Tools~/serve.mjs --root Build --port 8080
```

Open `http://127.0.0.1:8080/`. It serves static build files and PMTiles ranges,
supports GET/HEAD, includes COOP/COEP, and does not dynamically compress bytes.
Precompressed `.js.gz/.br`, `.data.gz/.br` and `.wasm.gz/.br` build resources are
served with their matching Content-Encoding. Use uncompressed development builds
if your Unity template uses special decompression-fallback conventions.
Stop with Ctrl+C. The server is for local testing, not production hardening.

For another device on your LAN, explicitly opt into listening on all interfaces:

```powershell
node Packages/com.00wz.geofoundry/Tools~/serve.mjs --root Build --host 0.0.0.0 --port 8080
```

Visit the computer's LAN IP instead of localhost; allow the port in your firewall
if necessary. Browser secure-context/threaded-WASM requirements may require HTTPS
for access from another device; HTTP localhost's secure-context exemption does
not extend to arbitrary LAN IPs. For a separate archive origin, `--cors` accepts
an explicit origin, e.g. `--cors http://localhost:9000`.

Quick check against an archive in the build:

```powershell
curl.exe -i -H "Range: bytes=0-126" http://127.0.0.1:8080/StreamingAssets/Maps/moscow.pmtiles
```

Expect 206, Content-Range starting with `bytes 0-126/`, and 127 body bytes.

## Prepare the data

The reader supports PMTiles v3 tile type 1 (MVT) and 2 (PNG), internal/tile
compression None or Gzip. Brotli, Zstd, unknown codes and other archive versions
produce explicit errors. PMTiles does not identify a PNG's height encoding:
DEM files **must contain Terrarium RGB**, not Mapbox Terrain-RGB or normal imagery.
Formula: `height = R*256 + G + B/256 - 32768`; alpha zero means NaN.
All DEM tiles must have the configured square dimensions (default 256).

For vectors, [Tippecanoe/Planetiler](https://docs.protomaps.com/pmtiles/create)
can produce PMTiles. Match the layers/attributes to your GeoFoundry handlers:
an arbitrary MVT schema is not automatically OpenMapTiles-compatible.

Existing MBTiles can be converted offline; SQLite is a preparation-tool concern,
not a Unity runtime dependency:

```sh
pmtiles convert moscow.mbtiles moscow.pmtiles
pmtiles show moscow.pmtiles
pmtiles extract regional.pmtiles moscow.pmtiles --bbox=37.0,55.1,38.3,56.1 --maxzoom=15
```

See the [official pmtiles CLI reference](https://docs.protomaps.com/pmtiles/cli).
The converter handles MBTiles TMS storage convention; do not flip PMTiles XYZ Y
again. Regions and zoom ranges should cover the map's intended navigation.

For DEM, [rio-rgbify](https://github.com/mapbox/rio-rgbify) can tile a raster into
PNG MBTiles with Terrarium-compatible base/interval, then the same converter
creates PMTiles:

```sh
rio rgbify -b -32768 -i 0.00390625 --min-z 0 --max-z 14 --format png dem.tif dem.mbtiles
pmtiles convert dem.mbtiles moscow-dem.pmtiles
pmtiles show moscow-dem.pmtiles
```

Check a known elevation and tile dimensions before using a new DEM dataset.
These are preparation recipes, not pipelines executed on the user's Moscow data
in this change. Preserve attribution/licences of the underlying map/elevation data.

## Limits and verification

Default reader limits: 16 MiB compressed/inflated tile, 1 MiB compressed/inflated
directory, 65536 entries/directory, directory depth at most 4; root is restricted
to the first 16 KiB by the v3 format. A reader caches the root and four parsed
leaf directories, serializes its reads, and does not cache tile payloads.
These limits bound individual work buffers; they are not a total application
memory budget. Constructor options can reduce reader/cache limits; HTTP ranges
remain limited to 16 MiB per request. Archives themselves can be much larger.

Filesystem archives use signed 64-bit offsets; web offsets are limited to exact
JavaScript integers (2^53-1). Gzip is processed in chunks with cancellation and
frame yields for larger results. PNG LoadImage is synchronous: cancellation is
checked before/after it, then during height conversion. It cannot interrupt the
native PNG decoder halfway through a call.

EditMode tests generate their own tiny MVT/PNG archives (no third-party fixtures).
They cover root/leaf lookup, XYZ, run/offset encoding, missing/empty DEM, malformed
headers/directories/gzip, unsupported features, cancellation and ownership.
See [local-pmtiles-verification.md](local-pmtiles-verification.md) for the
experiment results and validation scope. Temporary browser/IL2CPP probe projects,
scripts, fixtures and builds were removed after validation; the package's
EditMode tests remain. Untested configurations must not be inferred from a
passing desktop Chromium run.

Manual checklist for deployment:

1. Verify 206/Content-Range/no Content-Encoding on the actual archive URL.
2. Load both MVT and DEM in a WebGL build, compare known features/elevations.
3. Cancel navigation/load, then request tiles again; check Network and leak logs.
4. Test out-of-coverage DEM: default heights, no error/log spam.
5. Test production CORS, intended browsers/mobile hardware and application memory
   with both real archives. Safari/mobile Web and iOS remain device checks.
6. Android: test extraction cancellation, disposal and enough free storage on the
   actual device/APK. Only the prototype's successful extraction/Seek was device-tested.
