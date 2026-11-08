# GeoFoundry

A Unity package for building 3D maps from MVT vector tiles and DEM elevation data. What gets built from the data
is decided by layer handlers, so the package is not tied to a particular tile schema: a complete set of built-in
handlers covers all 16 layers of the OpenMapTiles schema (surfaces, roads, buildings, boundaries, labels and icons,
styled after the official OSM OpenMapTiles style), and other schemas get their own handlers. Heavy computation runs on Burst and the Job System, including in WebGL builds.

- **Documentation** — [Documentation~/index.md](Documentation~/index.md): requirements, installation, setup, Web builds,
  extension points, diagnostics.
- **Repository** — https://github.com/00wz/GeoFoundry
- **Changelog** — [CHANGELOG.md](CHANGELOG.md)

Start with the [Innsbruck sample](Samples~/Innsbruck/README.md): a configured
local map, terrain and editable preset copies imported through Package Manager.
It requires Input System and includes regional map data.
