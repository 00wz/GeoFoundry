# Local MVT and DEM archives

GeoFoundry loads MVT vector tiles and Terrarium PNG elevation tiles from PMTiles
v3 archives. Each source uses one archive and reads only the required ranges.
Map and elevation datasets are not included; supply archives with the schema,
coverage and licence appropriate for your project.

## Configure the bootstrapper

1. Create a config with **Create → GeoFoundry → Config** and assign it to a
   **GeoFoundry → Map Bootstrapper** in your scene.
2. Leave **Source Mode → Local PMTiles**, the default. Place your MVT archive at
   `Assets/StreamingAssets/Maps/vector.pmtiles` and Terrarium DEM archive at
   `Assets/StreamingAssets/Maps/dem.pmtiles`, or change **Vector Archive Path**
   and **Dem Archive Path** to match your files.
3. Assign the terrain and layer handlers as described in the
   [quickstart](index.md#4-create-the-config-and-the-component). The MVT layers
   must match the selected handlers; built-in handlers target OpenMapTiles.
4. Set **Tile Size Pixels** to the DEM tile dimensions (default 256). Disable
   **Enable Relief** or leave **Dem Archive Path** empty for flat terrain.
5. Enter Play mode. For WebGL, build and host with a Range-capable server as
   described below; Unity's standard Build and Run server is insufficient.

The source mode applies to both MVT and DEM. The config Inspector displays only
the selected mode's settings and preserves the hidden values when switching.
The bootstrapper owns its sources and releases them when disabled.

| Archive setting | Resolution |
| --- | --- |
| `Maps/vector.pmtiles` | Relative to Application.streamingAssetsPath on the current platform |
| Absolute filesystem path | Used directly on filesystem platforms; not portable across devices |
| Absolute HTTP(S) URL | Archive read through HTTP Range; useful for archives hosted separately from the build |

Use relative StreamingAssets paths for a portable packaged dataset. In WebGL the
result is a URL and requires HTTP Range even though the mode is called local.
Parent traversal in relative paths is rejected. Archives must remain unchanged
while a source is open.

To use Martin/TiTiler instead, select **Source Mode → Remote HTTP** and set
**Vector Tiles Endpoint Template** and **Dem Tiles Endpoint Template** with
`{z}/{x}/{y}` placeholders. The remote DEM is also optional; an empty template or
disabled relief gives flat terrain. **Max Concurrent Http Requests** limits the
shared remote downloader; it does not configure archive reads in local mode.

Configs saved before Source Mode was added use the new local default. To keep
their previous HTTP behavior, select Remote HTTP once. Existing endpoint values
are preserved; there is no automatic mode inference from those values.

## Custom source integration

A custom bootstrap can create the same sources directly:

```csharp
using GeoFoundry.TileIO.PmTiles;
using GeoFoundry.TileIO.Sources;
using Unity.Collections;

using var vectors = new PmTilesVectorTileSource(
    PmTilesByteSources.StreamingAsset("Maps/vector.pmtiles"));
using var dem = new PmTilesDemTileSource(
    PmTilesByteSources.StreamingAsset("Maps/dem.pmtiles"), tileSizePixels: 256);

// Called on the Unity main thread. Pass a cancellation token tied to your map/session.
using var tile = await vectors.GetTileAsync(coord, Allocator.Persistent, ct);
using var heights = await dem.GetHeightsAsync(coord, Allocator.Persistent, ct);
// A missing/empty DEM has heights.IsCreated == false; do not sample it.

// Keep the source-owning scope alive for the map session, rather than reopening per tile.
```

These sources can be passed to consumers accepting `IVectorTileSource` and
`IDemTileSource`.
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
Build and Run server.

On WebGL the byte source uses a small JS streaming-fetch plug-in. It rejects a
200 response at headers, before reading its body, and caps the actual returned
range. This avoids Unity's standard web loader reserving a buffer for the whole
archive before C# sees it. On other platforms HTTP ranges use UnityWebRequest.
There is deliberately no automatic full-file fallback, including for small files.

### Included development server

Requires Node.js 18 or newer, no npm packages. Run the script from the package's
`Tools~` folder and point `--root` at your web build directory. With this package
embedded at `Packages/com.00wz.geofoundry`, run from the Unity project root:

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
curl.exe -i -H "Range: bytes=0-126" http://127.0.0.1:8080/StreamingAssets/Maps/vector.pmtiles
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
Preserve attribution/licences of the underlying map/elevation data and verify
the output tiles before distributing them with your application. Preparation
tools run outside Unity and are not runtime dependencies.

## Limits and verification

Default reader limits: 16 MiB compressed/inflated tile, 1 MiB compressed/inflated
directory, 65536 entries/directory, directory depth at most 4; root is restricted
to the first 16 KiB by the v3 format. A reader caches the root and four parsed
leaf directories, serializes its reads, and does not cache tile payloads.
These limits bound individual work buffers; they are not a total application
memory budget. Constructor options can reduce reader/cache limits; HTTP ranges
remain limited to 16 MiB per request. Archives themselves can be much larger.
PMTiles DEM tile dimensions must be between 2 and 4096 pixels per side.

Filesystem archives use signed 64-bit offsets; web offsets are limited to exact
JavaScript integers (2^53-1). Gzip is processed in chunks with cancellation and
frame yields for larger results. PNG LoadImage is synchronous: cancellation is
checked before/after it, then during height conversion. It cannot interrupt the
native PNG decoder halfway through a call.

EditMode tests generate their own tiny MVT/PNG archives (no third-party fixtures).
They cover root/leaf lookup, XYZ, run/offset encoding, missing/empty DEM, malformed
headers/directories/gzip, unsupported features, cancellation and ownership.
Validated archive access: Unity 6000.4.8f1 Editor/Windows and an IL2CPP WebGL
development build in desktop Edge 157. Web checks covered MVT leaf directories,
gzip, PNG DEM, missing tiles, HTTP cancellation and rejection of HTTP 200.
Reading the last 64 KiB of a synthetic 1 GiB archive also passed; the entire
archive was not loaded. This does not establish performance for a particular
city dataset, mobile browser or full application scene. iOS and the complete
Android source lifecycle still require device validation.

Manual checklist for deployment:

1. Verify 206/Content-Range/no Content-Encoding on the actual archive URL.
2. Load both MVT and DEM in a WebGL build, compare known features/elevations.
3. Cancel navigation/load, then request tiles again; check Network and leak logs.
4. Test out-of-coverage DEM: default heights, no error/log spam.
5. Test production CORS, intended browsers/mobile hardware and application memory
   with both real archives. Safari/mobile Web and iOS remain device checks.
6. Android: test extraction cancellation, disposal and enough free storage on the
   actual device/APK. APK extraction and seeking have been checked separately;
   cancellation and repeated session cleanup still require device validation.
