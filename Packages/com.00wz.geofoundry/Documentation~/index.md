# GeoFoundry

A Unity package for building 3D maps from MVT vector tiles and DEM elevation data. What gets built from the data is decided by layer handlers, so the package is not tied to a particular tile schema: the built-in handlers (buildings, roads, water, landuse, landcover, parks) target the OpenMapTiles schema, and other schemas get their own handlers. Heavy computation runs on Burst and the Job System, including in WebGL builds.

> Status: in development. Working today: MVT and DEM tile loading, adaptive quadtree LOD with a memory budget, terrain elevation, buildings, roads and surfaces, the camera and floating origin. See [CHANGELOG.md](../CHANGELOG.md) for the change history.

## Requirements

- **Unity and URP.** Tested on Unity 6000.4.8f1 and URP 17.4 — this is also the minimum version declared in
  `package.json`. The APIs used (`Awaitable`) require at least Unity 6, but earlier Unity 6 and URP 17 versions
  have not been tested; the package shaders are based on Lit from URP 17.4.
- **Web (WebGL)** — Unity 6.4 or newer: this is the first version where Burst and the Job System are officially
  supported on Web.
- **Input System** — only for the `MapCameraRig` camera. Without it the package still works; the
  `GeoFoundry.CameraControl` assembly is simply not compiled.
- **An MVT vector tile server** — for example, [martin](https://github.com/maplibre/martin). Any schema works; the
  built-in handlers target OpenMapTiles (generated, for example, by [planetiler](https://github.com/onthegomap/planetiler)).
- **A Terrarium PNG DEM tile server** — for terrain elevation, for example [titiler](https://github.com/developmentseed/titiler).
  Without it the ground is flat.

## Quickstart

### 1. Install the package

Package Manager → **+** → **Add package from git URL**:

```
https://github.com/00wz/GeoFoundry.git?path=/Packages/com.00wz.geofoundry#v0.1.0
```

`#v0.1.0` pins the version. A local copy of the package can be added with **Add package from disk** → `package.json`.
Dependencies (Burst, Collections, Mathematics, URP — versions are listed in `package.json`) are installed automatically.

### 2. Allow HTTP (if your tile servers use http://)

Since Unity 2022, HTTP requests are blocked by default (`PlayerSettings.insecureHttpOption = NotAllowed`). If your martin/titiler servers are served over `http://` (intranet, local network), choose one of:

- **Tools → GeoFoundry → Allow HTTP (Development Builds)** — HTTP in development builds and Editor Play Mode (recommended).
- **Tools → GeoFoundry → Allow HTTP (Always — intranet only!)** — HTTP everywhere, including release builds.

If you forget, the Console shows the error `Non-secure network connections disabled in Player Settings`.

### 3. Configure the Web target

For Web builds only:

1. `Project Settings → Player → Web → Other Settings → Native C/C++ Multithreading = Enabled`.
2. `Project Settings → Player → Web → Resolution and Presentation → WebGL Template = **Default**`. No custom template is needed for COOP/COEP — Build & Run in Unity 6.4 sets the headers itself. For production hosting, configure the server as described in [web-deployment.md](web-deployment.md).
3. `Project Settings → Player → Web → Publishing Settings → Memory Size`: Initial ~256 MB, Maximum 2032 MB.

`WebBuildValidator` logs a warning if threadsSupport is disabled or HTTP requests are blocked, and fails the build if the Burst package is missing or too old.

### 4. Create the config and the component

1. `Create → GeoFoundry → Config` — create a `GeoFoundryConfig` ScriptableObject and set the URLs of your servers: `VectorTilesEndpointTemplate` (martin, required) and `DemTilesEndpointTemplate` (titiler; leave empty for no terrain elevation). Both are empty by default — without a vector tile URL the bootstrapper does not start and logs an error to the Console. Example templates: `http://host:3000/moscow/{z}/{x}/{y}` (martin), `http://host:8000/cog/tiles/WebMercatorQuad/{z}/{x}/{y}.png?url=/dem/dem.tif&algorithm=terrarium` (titiler).
2. In the config, assign a terrain handler and layer handlers (a ready-made set for the OpenMapTiles schema is in the package's `Presets/OpenMapTiles/`) and adjust the loading and memory limits if needed.
3. Create an empty GameObject at the origin (no rotation or scale — tiles become its children), add the `GeoFoundry → Map Bootstrapper` component and assign the config to it. The camera is optional — `Camera.main` is used if none is set.

## Architecture (asmdef layers)

```
GeoFoundry.Core           ← public API: coordinates and floating origin, MVT model and parser, all contracts
GeoFoundry.TileIO         → Core                ← TileDownloader, HTTP MVT/DEM sources, Terrarium decoder
GeoFoundry.Geometry       → Core                ← Burst geometry jobs, triangulation, projection, mesh building
GeoFoundry.Ground         → Core                ← ground grids (from DEM or flat), terrain height
GeoFoundry.Render         → Core, Geometry, URP ← layer and terrain handlers, URP shaders
GeoFoundry.Tiles          → Core, Ground        ← quadtree LOD, memory budget, tile lifecycle, debugging
GeoFoundry.CameraControl  → Core, Input System  ← MapCameraRig; compiled only if Input System is installed
GeoFoundry.Bootstrap      → Core, TileIO, Ground, Tiles ← GeoFoundryBootstrapper and GeoFoundryConfig
GeoFoundry.Editor         ← Web build validator, Tools → GeoFoundry menu
```

Implementations depend only on Core and know little about each other. Implementation details (Burst jobs, budget,
caches, camera navigators) are `internal`.

## Extending

All extension points are contracts in `GeoFoundry.Core`; a reference to that assembly is enough for a custom
implementation.

| What | Contract | Namespace |
|---|---|---|
| Custom map layer | `ILayerHandler` / `LayerHandlerSO`, `ILayerHandlerInstance`, `LayerLoadContext` | `GeoFoundry.Core.Handlers` |
| Custom terrain | `ITerrainHandler` / `TerrainHandlerSO`, `ITerrainHandlerInstance`, `TerrainLoadContext` | `GeoFoundry.Core.Handlers` |
| Custom data source | `IVectorTileSource`, `IDemTileSource` | `GeoFoundry.Core.Sources` |
| Custom tile selection | `ITileSelector`, `ITilePrioritizer` | `GeoFoundry.Core.TileSelection` |
| Custom camera | `IMapCameraBinding` — the bootstrapper finds the component on the camera and binds it to the map | `GeoFoundry.Core` |

Custom terrain elevation is plugged in through an `IDemTileSource` height source; the ground grid provider
(`GroundGridProvider`) is an internal mechanism with two implementations and is not meant to be extended. For
building geometry in your own handlers there are the `AsyncGeometryWorker` (`GeoFoundry.Geometry`) and
`MeshHierarchyInstance` (`GeoFoundry.Render.Layers`) helpers.

The built-in handlers for the OpenMapTiles schema (buildings, roads) live in `GeoFoundry.Render.Layers.OpenMapTiles`,
and the ready-made handler assets in `Presets/OpenMapTiles/`. For another schema, write your own handlers on their
model; `SurfacesLayerHandler` and `DefaultTerrainHandler` do not depend on the schema and can be reused as is.
Surface and building UVs are in meters: set the texture density with the material's Tiling.

## Diagnostics

To tune the tile budget (`Gpu Budget MB`, `Heap Budget MB`, `Max Concurrent Loads`) and the level of detail
(`MaxTileScreenSizePixels`) — all of them in the config — enable **Debug → Draw Debug Overlay** on the
bootstrapper. Tile frames and a statistics panel appear in the Scene view. The overlay works only in the Editor and
is off by default.

### Frame colors

| Color | Tile |
|---|---|
| Green | rendered |
| Yellow | loading, or queued for a free load slot |
| Red | empty: needed, but has no content because the load failed (the reason is in the log and in the `TileLoadFailed` event) |
| Cyan | admitted by the budget and in memory, but not rendered (for example, outside the view) |
| Magenta | denied by the budget — a hole is shown in its place |
| Gray | not selected: warm cache or cancelling build |

### Panel

- **Tiles** — rendered / empty (failed loads) / loaded; in parentheses, the warm cache: tiles that are loaded but
  not currently needed. They are evicted first when memory runs short.
- **Loads** — running + cancelling / slots (`Max Concurrent Loads`), and `queued`: admitted tiles waiting for a
  slot. A cancelling build holds its slot until the cancellation completes. If the queue keeps growing, tiles
  cannot keep up with the camera: add slots or raise `MaxTileScreenSizePixels`.
- **Budget** — admitted / denied by the budget. `limit reached` means the desired set did not fit: the farthest
  tiles are denied and holes are shown in their place.
- **GPU**, **Heap** — memory of resident tiles (loaded and building) against the pool limit, and the part of it
  that is still `in flight` (building). The budget is soft: a percentage slightly above 100 is normal — it allows one boundary
  tile plus the tiles in flight.
- **Ground grids** — shared ground grids. They are not counted in the budget, but on WebGL they live in the same
  heap.

### Custom HUD

The same data is available from code, including in player builds: `GeoFoundryBootstrapper.Manager.GetDebugSnapshot(list)`
returns the state of every tile (`TileDebugInfo`), `TileDebugSummary.From(list)` returns aggregates,
`Manager.Budget` and `Manager.MaxConcurrentLoads` return the limits, and `GeoFoundryBootstrapper.GroundGridMemoryBytes`
returns the ground grid memory. The snapshot is built only on request, in O(tiles): poll it every few frames
rather than every frame.

### Failed loads

The `GeoFoundryBootstrapper.TileLoadFailed` event (or `TileLifecycleManager.TileLoadFailed` with a custom
bootstrap) reports every failed tile load with the tile coordinates and the exception. A download error arrives as
a `TileDownloadException`: its `HttpStatus` tells "no data" (404) apart from a server or network failure. A
cancelled tile is not reported as a failure. The event is a notification only: what happens to the tile next is up
to the selector — `TileQuadtreeTraversal` shows a hole in its place and does not retry the load while the tile is
needed for coverage; once the tile is no longer needed, the failure is forgotten and the next request for that tile
loads it again.

```csharp
bootstrapper.TileLoadFailed += (coord, error) =>
{
    if (error is TileDownloadException { HttpStatus: 404 }) return; // no data for this tile, which is expected
    Debug.LogWarning($"Tile {coord} failed to load: {error.Message}");
};
```

## Running tests

`Window → General → Test Runner` → EditMode/PlayMode → Run All.

The tests do not access external networks: the downloader is tested against a local HTTP server (`LoopbackHttpServer`), everything else against test doubles of the sources.

## License

MIT — see [LICENSE.md](../LICENSE.md). Some algorithms are based on [streets-gl](https://github.com/StrandedKitty/streets-gl) (MIT); it and other third-party sources are listed in [Third Party Notices.md](../Third%20Party%20Notices.md).
