# GeoFoundry

A Unity package for building 3D maps from MVT vector tiles and DEM elevation data. What gets built from the data
is decided by layer handlers, so the package is not tied to a particular tile schema: a complete set of built-in
handlers covers all 16 layers of the OpenMapTiles schema (surfaces, roads, buildings, boundaries, labels and icons,
styled after the official OSM OpenMapTiles style), and other schemas get their own handlers. Heavy computation runs on Burst and the Job System, including in WebGL builds.

- **Documentation** — [Documentation~/index.md](Documentation~/index.md): requirements, installation, setup, Web builds,
  extension points, diagnostics.
- **Repository** — https://github.com/00wz/GeoFoundry
- **Changelog** — [CHANGELOG.md](CHANGELOG.md)

## License

MIT — see [LICENSE.md](LICENSE.md). Some algorithms are based on [streets-gl](https://github.com/StrandedKitty/streets-gl)
(MIT); it and other third-party sources are listed in [Third Party Notices.md](Third%20Party%20Notices.md). The
OpenMapTiles presets include material under other licenses: the style tables and icons are derived from the
OpenMapTiles style (CC-BY 4.0, icons based on OSM Carto, CC0) and the Noto Sans fonts are under the SIL Open Font
License 1.1. Maps using the presets must credit `© OpenMapTiles © OpenStreetMap contributors`.
