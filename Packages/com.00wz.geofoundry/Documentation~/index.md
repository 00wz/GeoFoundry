# GeoFoundry

A Unity package for building 3D maps from MVT vector tiles and DEM elevation data. What gets built from the data is decided by layer handlers, so the package is not tied to a particular tile schema: a complete set of built-in handlers covers all layers of the OpenMapTiles schema (surfaces, roads, buildings, boundaries, labels and icons), and other schemas get their own handlers. Heavy computation runs on Burst and the Job System, including in WebGL builds.

> Status: in development. Working today: MVT and DEM tile loading, adaptive quadtree LOD with a memory budget, terrain elevation, the full OpenMapTiles layer set (surfaces, roads, buildings, boundaries, labels and icons), the camera and floating origin. See [CHANGELOG.md](../CHANGELOG.md) for the change history.

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
GeoFoundry.Render         → Core, Geometry, URP ← schema-independent handlers, URP shaders
GeoFoundry.OpenMapTiles   → Core, Geometry, Render ← OpenMapTiles handlers, labels and icons
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
building geometry in your own handlers there are the `AsyncGeometryWorker` (`GeoFoundry.Geometry`; including the
ordered draw-list builders `BuildSurfaceMeshAsync(layer, SurfaceDrawItem[] …)` and `BuildLineMeshAsync`, and
`GroundAnchors` for placing objects on the ground) and `MeshHierarchyInstance` (`GeoFoundry.Render.Layers`) helpers.

Line meshes from `BuildLineMeshAsync` get their width from the `GeoFoundry/Line Lit` shader at the camera's pixel
size (see [Line widths](#line-widths)), which `MapViewShaderGlobals` (`GeoFoundry.Render`) publishes before each
camera renders. `GeoFoundryBootstrapper` creates it; a custom bootstrap creates it after its ground height provider,
calls `Enable()` and disposes it on shutdown. Without it, lines render at the width they were built with.

For another schema, write your own handlers on the model of the OpenMapTiles ones (see below);
`SurfacesLayerHandler` and `DefaultTerrainHandler` do not depend on the schema and can be reused as is. Surface and
building UVs are in meters: set the texture density with the material's Tiling.

## OpenMapTiles handlers

The package ships a complete set of handlers for the [OpenMapTiles](https://openmaptiles.org/schema/) 3.16 schema
(assembly `GeoFoundry.OpenMapTiles`, namespace `GeoFoundry.OpenMapTiles`), with ready-made assets in
`Presets/OpenMapTiles/`. Every layer of the schema has a handler, and every attribute that carries rendering
information is read. The default look follows the official "OSM OpenMapTiles" style (colors, widths, casings, dashes,
icons, label fonts and zooms), and every table is editable in the inspector of the handler asset.

| Layer | Handler | Output |
|---|---|---|
| `water` | `OmtWaterHandler` | fills by `class`; `intermittent` color; `brunnel=tunnel` hidden/dimmed |
| `waterway` | `OmtWaterwayHandler` | ribbons by `class`; dashed `intermittent` and tunnels; bridges (aqueducts) with casing; names along lines |
| `landcover` | `OmtLandcoverHandler` | fills by `subclass` with `class` fallbacks; outlines |
| `landuse` | `OmtLanduseHandler` | fills by `class` (`subclass` matchable); outlines; optional `access=no` fade |
| `park` | `OmtParkHandler` | faint fill, casing outline, names by `rank` |
| `boundary` | `OmtBoundaryHandler` | lines by `admin_level`; `disputed` dashed; `maritime` tinted/hidden; worldview filter on `claimed_by`; `aboriginal_lands` outlines |
| `aeroway` | `OmtAerowayHandler` | area fills, runway/taxiway lines, `ref` labels |
| `transportation` | `OmtTransportationHandler` | casing + fill + overlay ribbons by `class`/`subclass`; tunnels, ground and bridges ordered by `layer`; `ramp`, `service`, `surface`, `access`, `toll`, `indoor`, `level`; oneway arrows; pedestrian/bridge/pier areas |
| `building` | `OmtBuildingHandler` | terrain-conforming extrusion by `render_height`/`render_min_height`, `colour` tint, `hide_3d` |
| `water_name` | `OmtWaterNameHandler` | point labels by `class`, labels along lake center lines |
| `transportation_name` | `OmtTransportationNameHandler` | street names along lines, `ref` shields, junction labels |
| `place` | `OmtPlaceHandler` | names by `class` and `capital` (capital markers), placed by `rank` |
| `housenumber` | `OmtHousenumberHandler` | house numbers |
| `poi` | `OmtPoiHandler` | icons by `subclass`/`class` with names, zoom-gated by class, placed by `rank`; `agg_stop`, `indoor`, `level` filters |
| `mountain_peak` | `OmtMountainPeakHandler` | peak/volcano/saddle icons with name and elevation (m or ft by `customary_ft`); ridge/cliff lines |
| `aerodrome_label` | `OmtAerodromeLabelHandler` | airport markers with name and IATA/ICAO code |

### Setup

Add the assets from `Presets/OpenMapTiles/` (the `Omt*` handlers and `Terrain`) to `GeoFoundryConfig.LayerHandlers`
/ `TerrainHandler`. `OmtTextSettings` is shared by all label-producing handlers: it holds the fonts (Noto Sans
Regular/Bold/Italic are included), the name language preference (`name:en`, `name` by default; put e.g. `name:ru`
first for Russian names), the label and icon materials and the icon atlas (`OmtIconAtlas`, built from the style's
icons with `Tools~/omt-icons/build_atlas.py`). Keep its `Tile Screen Size Pixels` equal to
`GeoFoundryConfig.Lod.MaxTileScreenSizePixels` (both are 1500 by default): handlers cannot query the tile selection,
and label decluttering depends on how large tiles appear on screen (see [Widths and zooms](#widths-and-zooms)).

### Attribution

The default look is derived from the OpenMapTiles style (CC-BY 4.0) and OpenMapTiles data comes from OpenStreetMap
(ODbL), so an application showing the map must display a visible credit, for example
`© OpenMapTiles © OpenStreetMap contributors` in a corner of the screen. GeoFoundry does not draw it for you. See
[Third Party Notices.md](../Third%20Party%20Notices.md) for the licenses of the bundled style tables, icons and fonts.

### Rebuilding the icon atlas

`Tools~/omt-icons/build_atlas.py` rasterizes the SVG icons of the openmaptiles repository (`style/icons`) into
`omt-icons.png` + `omt-icons.json`. It needs Python 3 with Pillow and Inkscape 1.x:

```
python build_atlas.py <openmaptiles>/style/icons <package>/Presets/OpenMapTiles/Icons --inkscape <path to inkscape> --width 512
```

Icon names are the SVG file names without the extension; label rules refer to them (`{class}`/`{subclass}`
placeholders are replaced by the feature's values).

### Draw order

Draped layers use the ZTest Always shaders `GeoFoundry/Surface Lit` (fills) and `GeoFoundry/Line Lit` (lines; adds
widths at the camera's pixel size and dashes), so the material's Sorting Priority (`_QueueOffset`) decides which layer covers which. The preset materials
follow `OmtDrawOrder`; a handler logs a warning in the inspector if an assigned material deviates from it:

| Priority | Mesh | Priority | Mesh |
|---|---|---|---|
| −50 | terrain | −35 | aeroway lines |
| −48 | landuse fill | −32 | transportation areas |
| −47 | aeroway fill | −30 | transportation lines |
| −46 / −45 | landcover fill / outline | −28 | waterway bridges |
| −43 | landuse outline | −27 | mountain lines |
| −42 | park fill | −26 | boundary |
| −40 | water | −24 | park outline |
| −38 | waterway | 0 | buildings |

As in the style, land use and aeroway areas lie below the land cover, so a park or grass inside a residential area
stays visible. Inside one mesh the order of the features is the draw order (casings below fills, tunnels below ground
roads below bridges). Colors come from the style tables as vertex colors, so the preset materials have a white base
color. The alpha of a color is the style's fill or line opacity: both draped shaders blend by the vertex alpha
(staying in the opaque queue, where the Sorting Priority orders them), so semi-transparent fills such as the park
tint or intermittent water show the layers below. In the deferred path they blend into the GBuffer like URP's GBuffer
decals: always for `GeoFoundry/Line Lit`, and for `GeoFoundry/Surface Lit` when the material's **Deferred Blend** is
on (all layer presets; off for `DefaultTerrain`, the base layer that must write the whole GBuffer). Shaders of the
layers below need no convention: any opaque URP shader (Lit, Shader Graph) works as a base.

### Widths and zooms

Scene units are Web Mercator meters. Style sizes are screen pixels, and zooms are display zooms (the MapLibre
convention, where the world is `512 · 2^zoom` pixels wide).

<a id="line-widths"></a>Line widths are evaluated by the `GeoFoundry/Line Lit` shader every frame, as in MapLibre and
Mapbox: a real ground width (converted to Mercator meters at the tile's latitude), but at least a minimum width in
screen pixels at the camera's pixel size (`LineWidth`), plus the casing border on each side. The pixel size is the
one at the center of the view (`MapViewShaderGlobals`), one value for the whole scene, so a line has the same world
width on every tile whatever its zoom, including the coarse tiles shown close up while the detailed ones load, and
perspective thins distant lines. Up close the real widths win; from afar the minimum pixels keep lines visible. A
line rule's `MinZoom` is compared with the tile zoom when the tile is built (lower-zoom tiles skip the line) and
with the camera zoom when rendering (the line fades in over half a zoom). The ribbons are built and draped onto the
ground at the tile's pixel size; the shader resizes them along the ribbon and follows the ground's tangent plane.

Labels are screen-space. The tile selection shows a tile of zoom `Z` while it is between `S / 2` and `S` pixels wide
(`S` is `Lod.MaxTileScreenSizePixels`, mirrored in `OmtTextSettings.TileScreenSizePixels`), that is at display zooms
`Z + log2(S / 1024)` to `Z + log2(S / 512)`. Labels are decluttered per tile on display-zoom tiers covering that
range (plus one lower tier for the far side of tiles under a tilted camera), and, because OpenMapTiles tiles end at zoom 14 and are viewed much closer, down to
`OmtTextSettings.SourceMaxZoom + OverzoomLevels` on tiles of that zoom: each label gets the zoom from which it does
not collide with more important labels, and the `GeoFoundry/Label` shader fades it in from that zoom as the camera
approaches.

Labels behind hills and buildings are hidden; the anchor is pulled toward the camera by the material's `Depth Pull`,
so labels of POIs inside buildings stay visible. The label materials' `Occlusion` selects how:

- `Depth Texture` (default): as MapLibre does on terrain, the shader tests the anchor alone against the camera depth
  texture and fades the whole label when it is hidden, so the ground and buildings never cut a label. Needs **Depth
  Texture** enabled in the URP asset (or on the camera); on WebGL it costs a depth copy per frame. Cameras without the
  depth texture fall back to `Depth Buffer`.
- `Depth Buffer`: the whole label gets the depth of the pulled anchor and is depth-tested per pixel, with no extra
  cost, but under a tilted camera the ground or a building in front can cut off part of a label.

Label zooms follow the OpenMapTiles schema (3.x): a rule's minimum zoom is never below the tile zoom from which the
schema puts its class into the tiles (quarters and neighbourhoods, most POIs and house numbers only at z14, bays at
z13, airports other than international ones from z10, …), so labels fade in with the zoom instead of popping up with
the tile that first carries them. The schema's `rank` is the order within each zoom's label grid (the same POI may be
rank 1 at z13 and 5 at z14), so only the global ranks (up to 10 in `place`: countries, states, major cities) order
the labels; equal priorities are resolved by the label text, so tiles of different zooms pick the same winners.

### Limitations

- `OmtTextSettings.TileScreenSizePixels` is not synchronized with the LOD settings. If it is lower than
  `Lod.MaxTileScreenSizePixels`, tiles show fewer labels than would fit; if it is more than about twice as high,
  labels fade in only shortly before their tile is replaced (they flicker as the camera moves).
- Line ribbons resized by the shader follow the ground's tangent plane at each vertex, not the terrain between the
  vertices; on sharp terrain breaks far away the widened edge can be off by about a pixel. Distant lines thinner than
  a pixel are not widened (Mapbox's "dilute"); use MSAA or TAA against shimmering. Lines cast no shadows.
- The schema sends different labels at different zooms by design (z14 carries far more POIs, short street names
  and house numbers than z13), and tiles switch instantly: a label that exists on only one of two zoom levels appears
  or disappears with its tile. There is no time-based cross-fade between tiles.
- Labels are decluttered within a tile, across all its label layers (in the order the layers come in the tile;
  house numbers yield to the other layers); collisions across tile borders are not resolved. Duplicates from the
  tile buffer are removed (only points inside the tile extent are labeled). A label takes space only from the zoom
  at which it becomes visible.
- Text is laid out without complex shaping (Arabic joining, Indic scripts) or right-to-left reordering; scripts
  covered by the fonts (Latin, Cyrillic, Greek with Noto Sans) render correctly.
- Fill patterns of the style (forest leaves, wetland reeds, military hatching) are not reproduced. Zoom-dependent
  opacities (the medium-scale land cover fading in between zoom 7 and 10) are drawn at full opacity.
- In the deferred path a semi-transparent pixel keeps the smoothness, occlusion and material flags of the surface
  below (they are stored in the GBuffer alpha channels, which carry the coverage), as with URP's GBuffer decals; the
  presets use the same values on all draped materials. A custom semi-transparent draped shader must blend the same
  way to work in deferred (Shader Graph cannot; its transparent materials render forward-only).
- Overlapping features of one semi-transparent mesh blend twice where they overlap (as in MapLibre fills).
- Country names on either side of a border (`boundary.adm0_l`/`adm0_r`) and the `route_2..6_*` concurrencies of
  `transportation_name` are not drawn.


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
