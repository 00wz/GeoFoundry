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
`GeoFoundryLitForwardPass.hlsl` and `GeoFoundryLitGBufferPass.hlsl` are modified copies of the Lit shader from the
`com.unity.render-pipelines.universal` package, distributed under the
[Unity Companion License](https://unity.com/legal/licenses/unity-companion-license).
