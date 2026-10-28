# Third Party Notices

GeoFoundry includes third-party algorithms and derivatives of third-party code, listed below.

## streets-gl

Some GeoFoundry algorithms are based on the [streets-gl](https://github.com/StrandedKitty/streets-gl) project
(a 3D OpenStreetMap renderer in TypeScript/WebGL2) and were rewritten for Unity, Burst and the Job System. They
include camera navigation (orbit and free modes, zoom damping, projecting the cursor onto the ground), building
geometry generation and joining road segments at bends. GeoFoundry is not a port of streets-gl: its architecture,
tile loading, LOD, terrain and Unity integration are original.

```
MIT License

Copyright (c) 2020-2023 StrandedKitty

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## earcut

Polygon triangulation (`Runtime/Geometry/Earcut/NativeEarcut.cs`) implements the earcut algorithm. The
implementation was written from scratch for Burst and the Job System (a node pool in a `NativeList` instead of
managed objects, no exceptions on degenerate input). The algorithm originates from
[mapbox/earcut](https://github.com/mapbox/earcut) (JavaScript, ISC); the rewrite used its C# translation
`EarcutLibrary` from the [Mapbox Unity SDK](https://github.com/mapbox/mapbox-unity-sdk), published under MIT in SDK
versions up to and including 2.1.1 (`sdkproject/Assets/Mapbox/Unity/MeshGeneration/Modifiers/MeshModifiers/Earcut.cs`).

### mapbox/earcut

```
ISC License

Copyright (c) 2016, Mapbox

Permission to use, copy, modify, and/or distribute this software for any purpose
with or without fee is hereby granted, provided that the above copyright notice
and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND ISC DISCLAIMS ALL WARRANTIES WITH REGARD TO
THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS.
IN NO EVENT SHALL ISC BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR
CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA
OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

### Mapbox Unity SDK (`EarcutLibrary`, versions up to 2.1.1)

```
The MIT License (MIT)

Mapbox Unity SDK copyright (c) 2016-2017 Mapbox.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Universal Render Pipeline — Lit

The shaders `Runtime/Render/Shaders/GeoFoundryBuildingLit.shader`, `GeoFoundrySurfaceLit.shader`,
`GeoFoundryLineLit.shader`, `GeoFoundryLitForwardPass.hlsl` and `GeoFoundryLitGBufferPass.hlsl` are modified copies of the Lit shader from the
`com.unity.render-pipelines.universal` package, distributed under the
[Unity Companion License](https://unity.com/legal/licenses/unity-companion-license).

## OpenMapTiles style and icons

The default style tables of the OpenMapTiles handlers (`Runtime/OpenMapTiles/Common/OmtStyleDefaults.cs`,
`Runtime/OpenMapTiles/Labels/OmtLabelDefaults.cs`) and the icon atlas (`Presets/OpenMapTiles/Icons/omt-icons.png`,
`omt-icons.json`) are derived from the "OSM OpenMapTiles" style of
[openmaptiles/openmaptiles](https://github.com/openmaptiles/openmaptiles) (`layers/*/style.json`, `style/icons`).

```
Copyright (c) 2024, MapTiler.com & OpenMapTiles contributors.
```

The cartography and visual design of OpenMapTiles are licensed under the
[Creative Commons Attribution 4.0 International License (CC-BY 4.0)](https://creativecommons.org/licenses/by/4.0/).
Changes made in GeoFoundry: colors, line widths, dash patterns, label sizes, fonts, zoom ranges, layer filters,
label priorities and the layer order were transcribed into C# tables and adapted to 3D rendering (zoom-interpolated
pixel widths fitted with real ground widths and minimum on-screen widths, minimum zooms raised to the zooms of the
schema's data, fill patterns dropped, defaults added for classes the style does not draw); the SVG icons were rasterized at 2x, packed into one atlas, and the fill and line
pattern icons were left out. The schema code of OpenMapTiles (BSD 3-Clause) is not included.

The icons of the style are based on the symbols of [OpenStreetMap Carto](https://github.com/gravitystorm/openstreetmap-carto),
which are released under the [CC0 1.0 Public Domain Dedication](https://creativecommons.org/publicdomain/zero/1.0/).

Maps rendered with this style must visibly credit OpenMapTiles, and maps of OpenStreetMap data must credit
OpenStreetMap contributors (the data is licensed under the [ODbL](https://www.openstreetmap.org/copyright)), for
example: `© OpenMapTiles © OpenStreetMap contributors`.

## Noto Sans

`Presets/OpenMapTiles/Fonts/NotoSans-{Regular,Bold,Italic}.ttf` — Copyright 2022 The Noto Project Authors
(https://github.com/notofonts/latin-greek-cyrillic), licensed under the SIL Open Font License, Version 1.1; the
license text is in `Presets/OpenMapTiles/Fonts/OFL.txt`.
