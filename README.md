# GeoFoundry

A Unity package for building 3D maps from MVT vector tiles and DEM elevation data. What gets built from the data
is decided by layer handlers, so the package is not tied to a particular tile schema: the built-in handlers
(buildings, roads, water, landuse, landcover, parks) target the OpenMapTiles schema, and other schemas get their own
handlers. Heavy computation runs on Burst and the Job System, including in WebGL builds.

## Installation

Package Manager → **+** → **Add package from git URL**, then paste:

```
https://github.com/00wz/GeoFoundry.git?path=/Packages/com.00wz.geofoundry#v0.1.0
```

Or add this line to your project's `Packages/manifest.json`:

```json
"com.00wz.geofoundry": "https://github.com/00wz/GeoFoundry.git?path=/Packages/com.00wz.geofoundry#v0.1.0"
```

`#v0.1.0` pins the version; without it you get the latest state of the main branch.

## Requirements

- Unity 6000.4.8f1 and URP 17.4 — the tested version, also the minimum in `package.json`.
- An MVT vector tile server (the built-in handlers expect the OpenMapTiles schema) and, for terrain elevation, a
  Terrarium DEM tile server. The `MapCameraRig` camera requires the Input System package.

See the [requirements in the documentation](Packages/com.00wz.geofoundry/Documentation~/index.md#requirements) for details.

## Quick start

1. `Create → GeoFoundry → Config`: set the tile URLs and assign handlers (a ready-made set for the OpenMapTiles schema is in the package's `Presets/OpenMapTiles/`).
2. Add the `GeoFoundry → Map Bootstrapper` component to an empty GameObject at the origin and assign the config.
3. Optionally add `GeoFoundry → Map Camera Rig` to the camera.

See the [package documentation](Packages/com.00wz.geofoundry/Documentation~/index.md) for WebGL setup,
diagnostics and extension points (custom layers, data sources, tile selection, camera).

## Repository layout

The repository is a Unity project for developing the package. The package itself lives in
[`Packages/com.00wz.geofoundry`](Packages/com.00wz.geofoundry): code, tests (EditMode and PlayMode),
default handlers and documentation. The rest is the host project: the `Assets/Scenes/SampleScene.unity` scene
and URP settings.

## License

MIT — see [LICENSE](LICENSE). Some algorithms are based on [streets-gl](https://github.com/StrandedKitty/streets-gl)
(MIT); it and other third-party sources are listed in
[Third Party Notices](Packages/com.00wz.geofoundry/Third%20Party%20Notices.md).
