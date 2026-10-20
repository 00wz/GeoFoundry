# GeoFoundry

A Unity package for building 3D maps from MVT vector tiles and DEM elevation data. What gets built from the data
is decided by layer handlers, so the package is not tied to a particular tile schema: the built-in handlers
(buildings, roads, water, landuse, landcover, parks) target the OpenMapTiles schema, and other schemas get their own
handlers. Heavy computation runs on Burst and the Job System, including in WebGL builds.

- **Documentation** — [Documentation~/index.md](Documentation~/index.md): requirements, installation, setup, Web builds,
  extension points, diagnostics.
- **Repository** — https://github.com/00wz/GeoFoundry
- **Changelog** — [CHANGELOG.md](CHANGELOG.md)

## License

MIT — see [LICENSE.md](LICENSE.md). Some algorithms are based on [streets-gl](https://github.com/StrandedKitty/streets-gl)
(MIT); it and other third-party sources are listed in [Third Party Notices.md](Third%20Party%20Notices.md).
