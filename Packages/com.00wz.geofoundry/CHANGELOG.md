# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - Unreleased

Initial release.

### Added

- Importable Innsbruck sample: configured scene and regional PMTiles,
  editable presets/materials, camera/URP settings, visible attribution and automatic data installation.
  Mutable presets have one template in the sample; tests use isolated temporary imports.
- PMTiles reads in EditMode now yield through the Editor synchronization context instead of waiting for a PlayerLoop frame.

- Configurable bootstrap sources: Local PMTiles (default) or Remote HTTP for both MVT and DEM,
  with a mode-aware Config Inspector and optional DEM in either mode.

- Local PMTiles v3 MVT and Terrarium DEM sources, cancellable bounded byte-range reader,
  None/Gzip support, Android StreamingAssets extraction and WebGL HTTP Range transport.
  Includes a dependency-free Node development server and setup/data preparation documentation.

- HTTP loading of MVT vector tiles and Terrarium DEM tiles: a shared limit on concurrent requests, retries with
  backoff, cancellation.
- MVT parser producing a NativeArray representation for Burst jobs.
- Adaptive quadtree LOD driven by screen-space error, with GPU and heap memory budgets for tiles, load priorities
  and eviction.
- Terrain elevation from DEM (or flat ground without it) and projection of layer geometry onto the ground grid.
- A complete set of handlers for the OpenMapTiles 3.16 schema (`GeoFoundry.OpenMapTiles` assembly, assets in
  `Samples~/Innsbruck/Presets/OpenMapTiles/`) covering all 16 layers: surfaces, waterways, roads and railways with casings, dashes,
  tunnels and bridges, boundaries, extruded buildings, and screen-space labels and icons (place, POI, house numbers,
  street names and shields, water names, peaks, airports) styled after the official "OSM OpenMapTiles" style; any
  other schema or layer is supported through custom `ILayerHandler`s.
- Label rendering: runtime SDF glyph atlases (the engine's TextCore font engine, Noto Sans included; no
  TextMeshPro resources needed), an icon atlas built from the style's icons, per-tile decluttering across label
  layers over display-zoom tiers matched to the tile screen size (`OmtTileScreenSize`), and the
  `GeoFoundry/Label` shader (whole labels fade when their anchor is occluded, tested against the camera depth
  texture as in MapLibre, never cut by the ground or buildings; a per-pixel depth-buffer mode without the depth
  texture).
- `GeoFoundry/Line Lit` shader (Surface Lit with dashes; Surface Lit and Line Lit blend by the vertex alpha for
  semi-transparent style colors, in deferred like GBuffer decals, Surface Lit per material through "Deferred Blend")
  and ordered draw-list geometry: `AsyncGeometryWorker.BuildLineMeshAsync` (ribbons of their real width in meters), a
  draw-item overload of `BuildSurfaceMeshAsync`, `GroundAnchors`.
- OpenMapTiles lines with widths evaluated per frame at the camera's pixel size, as in MapLibre (`OmtLineWidth`,
  `GeoFoundry/OMT Line Lit`); the transportation lines by default at their real ground width only
  (`OmtLineWidthMode.RealWorld`, `OmtLineRule.RealWidthMeters` for lines the style sizes in pixels).
- Floating origin based on a double-precision Mercator pivot.
- `MapCameraRig` camera: orbit and free modes, desktop and touch input. Requires the Input System package;
  without it the camera assembly is skipped from compilation and the rest of the package still works.
- Extension points as contracts in the `GeoFoundry.Core` assembly: layer and terrain handlers, data sources,
  tile selection, camera binding (`IMapCameraBinding`).
- `GeoFoundryBootstrapper` and `GeoFoundryConfig` for setting up a map in a scene; a tile debug overlay and a state
  snapshot for a custom HUD.
- Web builds: Burst jobs on WASM threads, a build validator, a menu for allowing HTTP.
- The defaults of the OpenMapTiles handlers audited against the 3.16 schema and the style
  (`Documentation~/omt-schema-audit.md`).
- OpenMapTiles buildings fade their heights and colors in from the zoom 13 blocks (`GeoFoundry/OMT Building Lit`).
- Public geometry building blocks for custom handlers' jobs: `NativeGroundProjector`, `NativeTriangleMath`,
  `NativeMvtPolygonHelpers`, `NativeMvtLineHelpers`, `NativePolygonTriangulator`, `MeshPacking`.

### Changed

- Draped layer shaders (`GeoFoundry/Surface Lit`, `GeoFoundry/Line Lit`, `GeoFoundry/OMT Line Lit`) are depth-tested
  (`ZTest LEqual` with `Offset -2, -1`) and no longer write depth (`ZWrite Off`). The terrain is their occlusion base:
  layers behind hills are hidden regardless of Sorting Priority, and priority only orders coplanar layers.
- The terrain handler is required: the draped layers use the terrain depth for occlusion. `DefaultTerrain` now uses
  the stock URP Lit shader.
