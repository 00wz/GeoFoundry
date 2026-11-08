# Innsbruck local map

A configured map of Innsbruck with local MVT and Terrarium DEM archives, terrain,
OpenMapTiles layers, camera navigation and visible data credits. About 14 MB of
data covers 11.30–11.49° E and 47.22–47.34° N at zooms 0–14. DEM includes neighbor
tiles and uses 128×128 pixels. The start position is 11.4041° E, 47.2692° N.

## Import and run

1. Use Unity 6000.4.8f1+ with URP 17.4. Install **Input System** through Package
   Manager and enable **Input System Package (New)** or **Both** in Player Settings.
   Use **Linear** color space for the authored appearance.
2. In Package Manager, select **GeoFoundry → Samples → Innsbruck → Import**.
3. Wait for script compilation and asset import to finish. Open the imported
   `Scenes/Innsbruck.unity`, then press **Play**.

Package Manager copies the sample to `Assets/Samples/GeoFoundry/<version>/Innsbruck`.
The imported Editor script automatically copies archives from
`Data/Maps` to `Assets/StreamingAssets/GeoFoundry/Innsbruck`. It also checks this
installation before Play and builds, restoring missing files. There is no setup
menu to run. A nested StreamingAssets folder inside a sample would not be Unity's
special StreamingAssets root.

Existing identical files are reused. Differing existing files are never overwritten:
move them aside, or keep your own data/config outside the sample. Installation
does not open scenes or change packages, input settings, rendering or Web settings.

The scene needs a URP project. If needed, assign the included
`Settings/InnsbruckURP.asset` in **Project Settings → Graphics → Default Render
Pipeline** and in the active **Quality → Render Pipeline Asset**. Other quality
levels can override Graphics settings. Your project's current URP pipeline can
also render the scene; its appearance may differ.

## Navigation and customization

Left drag pans, right drag orbits, the wheel zooms. WASD moves, Shift increases
speed and Tab switches between ground orbit and free flight. Touch uses one-finger
pan, two-finger rotation/pitch and pinch zoom. Leaving the covered region does not
download more data; missing DEM uses the fallback terrain height.

Edit the imported `Configs`, `Presets/OpenMapTiles` and `Settings`. The package's
only mutable preset template is in this sample. Tests read the unimported template
and create temporary imports with new GUIDs, then remove them. They never test
your customized `Assets/Samples` copy. Fonts, icon images and shaders remain shared
package resources. Keep OmtTileScreenSize consistent with the config's LOD screen
size when tuning detail.

Back up customizations before re-importing. Do not duplicate the sample scripts
or assembly definitions inside Assets. Remove an older imported version before
importing a newer version containing the same scripts/assemblies.

## Web builds

Add the imported `Scenes/Innsbruck.unity` to your build profile. Enable **Player
Settings → Web → Native C/C++ Multithreading** for parallel jobs. Threaded builds
need COOP/COEP headers; archives need HTTP Range support. Build to a directory
such as `Build`, then serve it with the package's development server (Node.js 18+):

```powershell
node Packages/com.00wz.geofoundry/Tools~/serve.mjs --root Build --port 8080
```

Adjust the script path to your package's actual filesystem location and open
`http://127.0.0.1:8080/`. Unity's default Build and Run server does not provide
the required ranges in tested 6000.4.8f1. Hosted archives need 206 responses,
correct Content-Range, no dynamic archive compression, and CORS when cross-origin.
See the package's `Documentation~/local-pmtiles.md` and `web-deployment.md` for
server requirements and deployment settings.

A real IL2CPP WebGL build rendered the map, labels and credits in headless Edge.
That run reported unsupported URP shaders `Hidden/CoreSRP/CoreCopy`,
`Hidden/Universal Render Pipeline/StencilDitherMaskSeed`,
`Hidden/Universal/HDRDebugView` and
`Hidden/Universal Render Pipeline/Edge Adaptive Spatial Upsampling`.
URP reported that post-processing passes would not execute. Matching
post-processing appearance and warning-free startup need testing on your target
graphics devices. Android/iOS builds of this sample and touch controls are untested.

## Remove the sample

To remove the sample, delete the imported sample folder and its build-profile
scene entry, then remove `Assets/StreamingAssets/GeoFoundry/Innsbruck` if unused.
Keep customized assets/data first and restore any project settings you changed.
