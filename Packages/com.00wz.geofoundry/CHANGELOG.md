# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - Unreleased

Initial release.

### Added

- HTTP loading of MVT vector tiles and Terrarium DEM tiles: a shared limit on concurrent requests, retries with
  backoff, cancellation.
- MVT parser producing a NativeArray representation for Burst jobs.
- Adaptive quadtree LOD driven by screen-space error, with GPU and heap memory budgets for tiles, load priorities
  and eviction.
- Terrain elevation from DEM (or flat ground without it) and projection of layer geometry onto the ground grid.
- A complete set of handlers for the OpenMapTiles 3.16 schema (`GeoFoundry.OpenMapTiles` assembly, assets in
  `Presets/OpenMapTiles/`) covering all 16 layers: surfaces, waterways, roads and railways with casings, dashes,
  tunnels and bridges, boundaries, extruded buildings, and screen-space labels and icons (place, POI, house numbers,
  street names and shields, water names, peaks, airports) styled after the official "OSM OpenMapTiles" style; any
  other schema or layer is supported through custom `ILayerHandler`s.
- Label rendering: runtime SDF glyph atlases (the engine's TextCore font engine, Noto Sans included; no
  TextMeshPro resources needed), an icon atlas built from the style's icons, per-tile decluttering across label
  layers over display-zoom tiers, and the `GeoFoundry/Label` shader.
- `GeoFoundry/Line Lit` shader (Surface Lit with dashes) and ordered draw-list geometry:
  `AsyncGeometryWorker.BuildLineMeshAsync`, a draw-item overload of `BuildSurfaceMeshAsync`, `GroundAnchors`.
- Floating origin based on a double-precision Mercator pivot.
- `MapCameraRig` camera: orbit and free modes, desktop and touch input. Requires the Input System package;
  without it the camera assembly is skipped from compilation and the rest of the package still works.
- Extension points as contracts in the `GeoFoundry.Core` assembly: layer and terrain handlers, data sources,
  tile selection, camera binding (`IMapCameraBinding`).
- `GeoFoundryBootstrapper` and `GeoFoundryConfig` for setting up a map in a scene; a tile debug overlay and a state
  snapshot for a custom HUD.
- Web builds: Burst jobs on WASM threads, a build validator, a menu for allowing HTTP.
