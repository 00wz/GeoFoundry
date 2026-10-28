# OpenMapTiles schema and style audit

Audit of the built-in OpenMapTiles styles (`Runtime/OpenMapTiles`) against the OpenMapTiles 3.16 schema and its
"OSM OpenMapTiles" style. It lists every difference found, with the reference location and the proposed value. Each
row has an id (`TR-12`) so it can be approved or rejected individually; rows marked **Decision** need a choice.

## Sources and method

| Source | Used for |
|---|---|
| `layers/<layer>/<layer>.yaml`, `mapping.yaml`, `*.sql` (openmaptiles 3.16) | Attribute values and meaning; zoom from which each class/subclass is put into tiles |
| `layers/<layer>/style.json` | Style layers: `minzoom`/`maxzoom`, filters, colors, widths, text |
| `style/config.json`, `openmaptiles.yaml` | Layer list; style order is the `order` field of the layers |
| `http://192.168.253.55:3000/moscow` (planetiler 0.10.3, OpenMapTiles 3.16) | Check of the SQL zooms on real tiles |

**Tile scan.** A minimal MVT decoder (gzip + protobuf by hand, Python standard library only) read 1649 tiles:
z0–z12 over 36.8–38.4° E, 55.1–56.4° N (Moscow and its region) and z13–z14 over 37.35–37.87° E, 55.57–55.92° N
(the city). It recorded, per layer, the zooms and geometry types of every class/subclass and the values of every
attribute. 0 tiles failed. Where the scan differs from the SQL, the SQL is used and the difference is listed in
[Port differences](#port-differences-planetiler-vs-sql).

**Source independence.** The package is not tied to a tile source. Only the schema is assumed: the reference SQL
defines the content of each zoom, and "all at z14" means z14 and every deeper zoom a source may produce
(`openmaptiles.yaml` `maxzoom: 14` is the reference build, not a limit of the schema). No proposal depends on the
deepest zoom of a particular source.

**Data zoom.** "Data zoom" below is the first tile zoom at which the SQL puts the class into tiles with only
generalization filters (area, length, label grid). Smaller subsets that come earlier (Natural Earth ranked places,
route members, very large areas) are noted but do not lower it. The rule kept from the task: the final `MinZoom` of
every fill, line and label rule is not below the data zoom. The style may start later, never earlier.

**Widths.** The style gives screen pixels `S(z)`, interpolated between stops (`base` 1.2–1.4 for roads). Our
`LineWidth` is `max(R · k / mpp(z), P)` pixels: real meters `R` (scaled to Mercator by `k = 1/cos(latitude)`) and a
minimum of `P` pixels; `mpp(z) = 40075016.686 / (512 · 2^z)` (MapLibre convention, `OmtZoom.MetersPerPixel`). The fit
chooses `R` (0.5 m steps) and `P` (0.1 px steps) that minimize the largest ratio error `|log2(W/S)|` over z12–z16
(0.25 steps) at the reference latitude 55.75° (k = 1.777). Casings: the style's outline layer is a full width `O(z)`,
so the per-side casing is `(O − S) / 2`, fitted the same way to `CasingMeters`/`CasingPixels`. A real-meter width
doubles per zoom while the style grows by 1.2–1.4 per zoom, so the best fit still errs by ×1.2–1.6 at some zoom of
z12–z16. Below z12 the width stays at `P`, see **Decision G-W**.

**Text sizes.** Labels have one size. It is the style's `text-size` at z14, or at the nearest zoom where the style
layer is visible (`clamp(14, minzoom, maxzoom − 1)`).

**Fonts.** The style uses Open Sans stacks; we ship Noto Sans Regular/Bold/Italic. Semibold and Bold map to Bold,
Italic and Semibold Italic map to Italic, Regular stays Regular.

## Decisions

| Id | Decision |
|---|---|
| G-1 | A: lines are not filtered by tile zoom; fills keep the tile filter (a fill rule below its `MinZoom` falls through to the next rule) |
| G-W | Widths fitted over z12–z16 for all rules; thicker lines below z12 are a documented limitation (the width depends only on the camera zoom, so low-zoom tiles shown close up are not affected) |
| LC-4 | Marsh keeps the wood color |
| BD-2 | Admin levels 5–10 hidden |
| WN-1 | Lakes, bays and other points from z15 as in the style; ocean, sea and strait keep their zooms |
| TN-1 | By the style: road names from z11, limited by the data zoom (z12, motorway/trunk 11, paths 13) |
| AD-2 | Airports from z11 (the text zoom); the icon-only stage is a limitation |

## Global findings

| Id | Item | Ours | Reference | Proposed |
|---|---|---|---|---|
| G-1 | **Decision**: tile filter of rules | `Zoom < rule.MinZoom` skips the rule by **tile** zoom (`OmtItemJobs.cs:129`, `:211`, `OmtTransportationHandler.cs:244`). A rule whose `MinZoom` is above the deepest zoom of the source is never drawn, even when that source's tiles are viewed closer (overzoom). With a z14 source this already hides every rule at 15+; with a z12 source every rule at 13+ | Style `minzoom` is a **display** zoom: landcover outline 15 (`landcover/style.json:758`), steps/path outlines 15 (`transportation/style.json:3562`–`3769`), oneway 15 (`:8709`), landcover park 13 (`:243`). The handlers do not know the source's maximum zoom (`LayerLoadContext` has no such field; `TileQuadtreeConfig.MaxZoom` is Core configuration) | **A** (recommended): lines are not filtered by tile zoom at all; the line shader already hides and fades them by camera zoom (`LineShaderMath.HalfWidth`), as MapLibre does, whatever the source. Cost: lines of rules whose style zoom is above the tile zoom are built and stay invisible (e.g. path casings in z13 tiles). Fills have no fade in the surface shader and keep the tile filter; a fill rule above the source's deepest zoom stays hidden (Limitations; style fill zooms are ≤ 13). **B**: a "source max zoom" setting in the OpenMapTiles assembly, mirrored by hand from `TileQuadtreeConfig.MaxZoom` like `OmtTextSettings.SourceMaxZoom`, and the filter `Zoom < min(MinZoom, SourceMaxZoom)` for fills and lines. **C**: the deepest zoom passed in `LayerLoadContext` (Core API change, needs your approval) |
| G-2 | Casing zoom | Casing appears with its line | Outline layers often start later than the fill: minor/service/track outline 13 (`transportation/style.json:2949`, `:2438`, `:2497`), path/steps outlines 15 | New `OmtLineRule.CasingMinZoom` (0 = with the line); the casing item gets `max(MinZoom, CasingMinZoom)` |
| G-3 | Modifier conditions | `OmtModifier` tests one key | `agg_stop` needs "stop subclass AND no agg_stop" (POI-1); ramps and waterway tunnels need their own minimum zoom (TR-30, WW-6) | `OmtModifier` gets an optional second condition (AND) and an action `MinZoom` that raises the item's minimum zoom |
| G-4 | Per-rule rank filter | Rank filter per handler only (`OmtParkHandler._maxLabelRank`, `OmtPoiHandler._maxRank`) | `State labels` `rank < 3` (`place/style.json:236`), `National park labels` `rank <= 2` (`park/style.json:56`) | New `OmtLabelRule.MaxRank` (0 = no limit); Natural Earth ranks of states and parks are global (not grid ranks) |
| G-W | **Decision**: width fit range | — | — | **A** (as asked): fit over z12–z16. Lines visible below z12 then keep `P` pixels: motorway 8.8 px at z6 (style 0.5 px), river 2.1 px at z6 (style 0.5 px). **B**: for rules visible below z12, fit over z8–z16 (boundaries z5–z16): motorway R 21.5 m / P 3.7 px, error ×2.1 on z12–z16 instead of ×1.35. I recommend **A** for rules with data zoom ≥ 11 and **B** for motorway, trunk, primary, secondary, links, rail, ferry, river, boundaries |

## Draw order

The style order (`order` in `layers/*/style.json`): landuse fills 1–11, aeroway fill 12, landcover 13–46 (with land use
stadium/pitch 21–22 inside), waterway 47–51, water 52–53, landcover patterns 54, park lines 55–56, aeroway lines 57–58,
landuse pattern/outlines 59–61, building 62, transportation 63–202 (river bridges 158–159 between rail and road
bridges), boundary 203–205, labels 206–256.

| Id | Material | Ours (`OmtDrawOrder.cs`) | Style order | Proposed |
|---|---|---|---|---|
| D-1 | Waterway | −38, above water | 47–51, **below** water 52–53 (river lines are covered by riverbank polygons) | −44 |
| D-2 | Water | −40 | 52–53 | −42 |
| D-3 | ParkFill (ours, not in the style) | −42, below water | — | −43 (still below water, above waterway) |
| D-4 | ParkOutline | −24, above roads and boundaries | 55–56, below aeroway lines and roads | −40 |
| D-5 | AerowayLines | −35 | 57–58 | −38 |
| D-6 | LanduseOutline | −43, below water | 60–61, above water, park and aeroway lines | −36 |
| D-7 | Unchanged | Terrain −50, Landuse −48, AerowayFill −47, Landcover −46, LandcoverOutline −45, TransportationAreas −32, Transportation −30, WaterwayBridges −28, MountainLines −27, Boundary −26, Building 0 | — | Material `_QueueOffset` and `m_CustomRenderQueue` of `Presets/OpenMapTiles/Materials` updated with D-1…D-6 |

Not reproducible: stadium/pitch between land cover classes (21–22), and river bridges between rail and road bridges
(transportation is one mesh; the bridge mesh stays above all roads).

## Label priorities

Within a handler, lower `Priority` is placed first. MapLibre places the upper style layer first, so priorities follow
the reverse style order.

| Id | Layer | Ours | Style order (first placed → last) | Proposed |
|---|---|---|---|---|
| P-1 | place | continent 0, country 1, state/province 2, capital 3, city 4, town 5, village 6, suburb/borough/island/aboriginal 7, quarter/neighbourhood/hamlet/islet 8, isolated_dwelling 9 | Country 256 → Capital 255 → City 254 → State 253 → Town 252 → Village 251 → Other labels 245 (hamlet, island, neighbourhood, suburb) | continent 0, country 1, capital 2, city 3, state/province 4, town 5, village 6, Other labels classes 7, unstyled classes (borough, quarter, isolated_dwelling, aboriginal_lands, fallback) 8 |
| P-2 | poi | 0–4 by group | Zoo 248, Local park 246, Train 230, Mall 229, Harbor 228, Bus station 227, Bus stop 226, Place of worship 225, Accommodation 224, Campsite 223, Health 222, Transport 221, Attraction 220, Cultural 219, Public 218, Food 217, Ferry 216, Sport 215, Outdoor 214, Education 213, Mortuary 212, Waste 211, Shop 210 | 0…22 in that order, fallback 23 |
| P-3 | transportation_name | names 0–7 by class, shields 0–4, junction 3 | Shields 241–237 (motorway → tertiary) → Road labels 236 → Ferry labels 235 | shields 0–4, road names 5–12 by class importance (tie-break; the style has none), ferry 13, junction (not styled) 14 |
| P-4 | mountain_peak | volcano/peak 0, saddle 1, ridge/arete 2, cliff 3 | Mountain peak labels 250 → Volcano 249 | peak/saddle 0, volcano 1, ridge/arete/cliff (not styled) 2 |
| P-5 | aeroway | runway 0, gate/taxiway 1 | Airport gate labels 244 → Airport labels 243 | gate 0, runway/taxiway 1 |
| P-6 | water_name | points 0–4, lines 3 | Water labels 208 → Lakeline labels 207 | points 0, lines 1 |

Between layers the placement order is the order of the layers in the tile (this source writes them alphabetically),
not the style order (labels of `place` over `mountain_peak` over `poi`/`park` over `aeroway` over
`aerodrome_label` over `transportation_name` over `poi` over `housenumber` over `water_name` over `waterway`).
This is outside the task (it needs a Core change) and is recorded in the Limitations.

## water

Data: `water.sql:547` — z0–z5 Natural Earth (ocean, lake), z6+ OSM. Scan: ocean z0–4, lake z2+, river z6+, pond z7+,
dock/swimming_pool z12+; intermittent from z10. All 6 classes are covered by the generic rule.

| Id | Item | Ours | Reference | Proposed |
|---|---|---|---|---|
| WA-1 | Fill, intermittent, tunnels | `hsl(194,45%,77%)`; intermittent `hsl(205,91%,83%)` α0.85; tunnels hidden | `water/style.json:4`, `:27` | Unchanged (matches) |
| WA-2 | `brunnel=bridge` | Not used | Not styled | Unchanged |

## waterway

Data: `waterway.yaml` / `waterway.sql:318` — z3–z8 Natural Earth rivers, z9–z11 river only, z12 + canal, z13+ all
classes. Scan: river z3+, canal z12+, stream/drain/ditch z13+, brunnel z12+, intermittent z12+.

| Id | Item | Ours (`OmtStyleDefaults.cs:213`) | Reference | Proposed |
|---|---|---|---|---|
| WW-1 | MinZoom river / canal / stream, drain, ditch, fallback | 0 for all (below data) | data 3 / 12 / 13 | 3 / 12 / 13 |
| WW-2 | Width river | 10 m / 1.2 px | `River` 0.5@11 → 20@20, base 1.2 (`waterway/style.json:44`) | A: 7.5 m / 2.1 px; B: 9 m / 0.3 px |
| WW-3 | Width canal, stream, drain, ditch, fallback | 8 / 2.5 / 1.5 / 1.5 / 2 m, 0.5–1 px | `Other waterway` (all non-river) 0.5@13 → 6@20, base 1.3 (`:139`) | 1.5 m / 0.6 px for all |
| WW-4 | Intermittent dash | (3, 2) for all | river (3, 2) (`:90`), others (4, 3) (`:185`) | river (3, 2), others (4, 3): two handler settings instead of one |
| WW-5 | Tunnels | Dim (faded toward white) + dash (2, 4), all zooms | `River tunnel`: same color, dash (2, 4), width of `Other waterway`, **minzoom 14** (`:4`) | Default `Show` + dash (2, 4); item minimum zoom 14 through the `MinZoom` modifier (G-3) |
| WW-6 | Bridges | Fill `hsl(204,92%,75%)`, casing `hsl(0,0%,73%)` 1 px / 1.5 m, widths of the class | `River bridge` 0.5@13 → 10@20 base 1.3; outline per side 0.5@12 → 12@20 base 1.6 (`:235`, `:288`) | Fill 2 m / 0.7 px for all classes; casing 1.5 m / 0.7 px |
| WW-7 | Labels | river 9, canal 12, stream 14, other 15; size 11/10 | `River labels`: all LineStrings, minzoom 3, size 10@11 → 12@13, italic, max width 5, spacing 400 (`:328`) | river 3, canal 12, others 13 (data); size 12 |

## landcover

Data: `landcover.sql:253` — z0–z6 only Natural Earth glaciers and ice shelves (`class=ice`), OSM areas from z7
(`osm_landcover_gen_z7`, `:319`). Scan: all classes from z7 except vineyard/beach/sand z12, flowerbed/shrubbery/
reedbed/scree z14 (area filters).

| Id | Item | Ours (`OmtStyleDefaults.cs:40`) | Reference | Proposed |
|---|---|---|---|---|
| LC-1 | MinZoom of class fallbacks wetland, sand, grass, rock, wood, farmland | 0 (below data) | data 7; style medium scale layers no minzoom, **maxzoom 13**, opacity 0.5@7 → 1@10 (`landcover/style.json:4`–`184`) | 7. Maxzoom and opacity ramp not reproducible for fills (Limitations) |
| LC-2 | Ice fill MinZoom | 0 | `Glacier` minzoom 5 (`:809`) | 5 |
| LC-3 | Subclass rules not in the style: flowerbed, bog, wet_meadow, shrubbery, fell, tundra, saltern | Own colors (grass, wood, scrub, heath, `hsl(194,25%,85%)`) | The style draws them only through the medium-scale class layers (class color) | Remove; they fall back to the class rule (same as the style up to z13) |
| LC-4 | **Decision**: marsh | wood color | `Marsh` `hsl(60,100%,50%)` (bright yellow), z9 (`:220`); from z13 covered by the `wetland_marsh` pattern | Transcribe the yellow (exact), or keep the wood color (pattern not reproducible). I recommend keeping wood and listing it |
| LC-5 | Swamp, wetland | wood color, order 27 | `Wetland and swamp` (`:359`) | Unchanged |
| LC-6 | Outline MinZoom | 14 | `Landcover outline` minzoom 15 (`:758`) | 15 (needs G-1 A or B) |
| LC-7 | Ice outline | 1.2 px, dash (4, 2) | 1@5 → 1.5@10, dash (1,0)@5 → (4,2)@10 (`:830`) | 1.5 px, dash (4, 2) |
| LC-8 | Fill patterns | — | `Landcover patterns` (`:882`), `Garden pattern` (`:289`) | Not reproducible (Limitations) |

## landuse

Data: `landuse.sql:255` — z4–z5 Natural Earth urban areas (residential); z6–z8 generalized tables keep only
residential, suburb, quarter, neighbourhood (`mapping.yaml:13`, `landuse_polygon_gen_z8` filter); all classes from z9.
Scan agrees; the port also writes classes outside the schema (allotments, construction, education, healthcare,
grass, …), which have no rule and are not drawn (as in the style).

| Id | Item | Ours (`OmtStyleDefaults.cs:107`) | Reference | Proposed |
|---|---|---|---|---|
| LU-1 | MinZoom of cemetery, military, railway, garages, dam, quarry, industrial, retail, commercial, education, stadium, playground, pitch, track | 7 (below data) | data 9; style 7 (`landuse/style.json:44`–`370`) | 9 |
| LU-2 | Residential, suburb, quarter, neighbourhood | 6, `hsl(0,2%,88%)` | `Residential` minzoom 6, `class in residential, suburbs, neighbourhood` (`:4`); `suburbs` is a typo, so suburb and quarter are not drawn by the style | Unchanged (intent of the style); typo noted |
| LU-3 | Library, bus_station, parking | Own (education color, grays) | Not styled | Keep as deliberate extensions (data z9) |
| LU-4 | Commercial outline | Missing | `hsl(2,49%,90%)`, 0.5 px (`:459`) | Add |
| LU-5 | Theme park, zoo outline | `hsl(330,100%,20%)` α0.3, 3 px | `Landuse outline` 1 px opaque + `Theme park` 3.5@9 → 5.5@14 px, opacity 0.1@9 → 0.3@12, offset 2 (`:605`) | 5.5 px α0.3 with an opaque overlay of 1 px (fraction 0.18); offset not reproducible |
| LU-6 | Patterns of military, cemetery, quarry | — | `Landuse pattern` (`:400`) | Not reproducible |

## mountain_peak

Data: `mountain_peak.sql:80` — points from z7 with `rank <= 5` per grid cell, all at z14; lines (ridge, cliff,
arete) from z13 (`:115`). Scan: peak z7+, cliff lines z13+.

| Id | Item | Ours | Reference | Proposed |
|---|---|---|---|---|
| MP-1 | Line MinZoom | 12 (below data) | data 13; no line layer in the style | 13; lines stay a deliberate extension |
| MP-2 | Peak/volcano/saddle MinZoom | peak/volcano 11, saddle 12 | `Mountain peak labels`/`Volcano peak labels`: no minzoom, **maxzoom 16**, text only from z12 (`text-field` stops 6:" ", 12:"{name} {ele}m"), filter `has ele` (`mountain_peak/style.json:4`, `:63`) | 12, MaxZoom 16; icon-only stage z7–z11 not reproducible (Limitations) |
| MP-3 | Filter `has ele` | All peaks labeled | Only peaks with `ele` | Point pass requires `ele` (`RequireKey`) |
| MP-4 | Saddle icon | `saddle` | `peak` (Mountain peak labels covers all non-cliff, non-volcano classes) | `peak` |
| MP-5 | Text | name, line break, `ele m`, size 10, colors match | `{name} {ele}m`, size 10, max width 6, offset 0.5 | Unchanged (the line break keeps the label compact) |
| MP-6 | Ridge, arete, cliff labels | Italic along lines from 13/13/14 | Not styled (cliff excluded) | Keep as extensions, MinZoom ≥ 13 (data) |

## park

Data: `park.sql:4` — polygons from z4 (dissolved), label points from z5 with area filters; `rank` is the grid rank.
Scan: classes include many Russian `protection_title` values (expected by `park.yaml`); the generic rule covers them.

| Id | Item | Ours (`OmtStyleDefaults.cs:175`) | Reference | Proposed |
|---|---|---|---|---|
| PK-1 | Outline | `hsl(107,34%,45%)` 1 px + casing `hsl(103,36%,66%)` α0.8 1.2 px, from 8 | `National parks` `hsl(103,36%,66%)` α0.8, 1.2@8 → 1.5@9 → 3.6@10, offset 1, from 8 (`park/style.json:4`); `National park outline` `hsl(107,34%,45%)` 1.5 px from **14** (`:41`) | Ribbon `hsl(103,36%,66%)` α0.8 3.6 px from 8, overlay `hsl(107,34%,45%)` fraction 0.42 (1.5 px); the overlay appears from 8, not 14, and the offset is not reproducible |
| PK-2 | Fill (15 % tint) | Own | Not styled | Unchanged (documented extension) |
| PK-3 | Labels | MinZoom 7, no maxzoom, no rank filter, halo `hsl(110,83%,88%)` 1 px, max width 8 | `National park labels` 7–12, `rank <= 2`, size 12, semibold italic, color 46 % → 25 %, halo `hsl(110,83%,88%)` 0.3 px, spacing 150 (`:56`) | MaxZoom 12, `MaxRank 2` (G-4), halo 0.3 px, max width 10 (MapLibre default) |

## boundary

Data: `boundary.sql:322`–`621` — admin_level 2 (Natural Earth) from z0, admin 4 (Natural Earth states) from z1,
maritime admin 2 at z4, OSM: admin ≤ 4 from z5 (`:503`), admin 5–6 from z10 (`boundary_z10` from
`osm_border_linestring_gen_z10`, which keeps 2–6, `:93`; z7–z9 tables keep 2–4, `:113`), admin 7–8 from z12 (`:83`),
admin 9–10 at z13 (`:73`). Scan: admin 3 z5–6, 5–6 from z9, 8 from z11, 9–10 at z12 (port differences).

| Id | Item | Ours (`OmtStyleDefaults.cs:233`) | Reference | Proposed |
|---|---|---|---|---|
| BD-1 | MinZoom admin 3 / 5–6 / 7–8 / 9–10 | 3 / 8 / 10 / 11 (below data) | data 5 / 10 / 12 / 13 | 5 / 10 / 12 / 13 |
| BD-2 | **Decision**: admin 5–10 | 5–6 visible (dashed), 7–10 hidden | Not styled | Hide 5–6 too (exact), or keep them visible as a documented extension. I recommend hiding (matches the style) |
| BD-3 | Admin 3–4 | `hsl(301,23%,42%)` 1.2 px, dash (5, 3), from 3 | `Other border` 0.4@4 → 0.7@5 → 1.6@12, opacity 0.5@3 → 1@10, filter `maritime = 0` (`boundary/style.json:4`) | 1.6 px (A) or 1 px (B, z5–z16); maritime 3–4 hidden |
| BD-4 | Admin 2 | `hsl(303,17%,56%)` 2 px | `Country border` 0.5@3 → 1.2@5 → 3@12, maritime not filtered (`:67`); disputed `hsl(246,17%,56%)` dash (4, 3) (`:115`) | 3 px (A) or 1.9 px (B); maritime admin 2 drawn like land borders |
| BD-5 | Maritime tint | `hsl(205,45%,65%)` for all levels | Not styled | Remove the tint: admin 2 as land, admin 3–4 hidden (`maritime=1`, BD-3) |
| BD-6 | `claimed_by` | Hidden without a worldview | Not filtered by the style | Unchanged (OSM worldview handling) |
| BD-7 | aboriginal_lands outline | Own, from 4 | data z4 (`osm_boundary_polygon_gen_z4`); not styled | Unchanged |

## aeroway

Data: `aeroway.sql:4` — lines and polygons from z10, points (gates) from z14. Scan agrees (classes aerodrome, apron,
helipad, heliport, runway, taxiway). The layer has no `name` field (`aeroway.yaml`), only `ref` and `class`.

| Id | Item | Ours (`OmtStyleDefaults.cs:188`) | Reference | Proposed |
|---|---|---|---|---|
| AW-1 | Fill colors | aerodrome/heliport `hsl(53,16%,89%)`, apron `hsl(240,8%,88%)`, runway/taxiway/helipad `hsl(234,25%,76%)` | `Aeroway`: one fill for all polygons, `hsl(240,8%,88%)`@6 → `hsl(53,16%,89%)`@12, minzoom 11 (`aeroway/style.json:4`) | `hsl(53,16%,89%)` for all classes |
| AW-2 | Line widths runway / taxiway | 45 m / 3 px, 20 m / 1 px | `Runway` 3@11 → 48@20, `Taxiway` 1@11 → 24@20, base 1.2 (`:36`, `:78`) | 18.5 m / 7.5 px, 9 m / 3.1 px |
| AW-3 | Runway/taxiway labels | `ref` along the line, runway 14, taxiway 15, size 11/10 | `Airport labels`: `name` (always empty: the layer has no name), minzoom 14, size 9@15 → 15@19 (`:120`) | Keep `ref` labels (deliberate extension), taxiway 14, size 9 |
| AW-4 | Gate labels | 16.5, size 10, regular | `Airport gate labels` 16.5, `ref`, size 9@17 → 15@19, semibold (`:180`) | Size 9, bold |

## transportation

Data (SQL): motorway z4 (national network, `update_transportation_merge.sql:1016`), trunk z4–z6 (national network),
motorway/trunk z7 (`:823`), primary z8 (`:1452`), secondary z9 (gen_z9/z10 exclude tertiary, `:395`), tertiary,
busway, bus_guideway z11 (`mapping.yaml:88`), minor, raceway z12 (`class.sql:62`), service z13 except driveway and
parking_aisle (`class.sql:81`), track/path z12 for route members, z13 named, z14 all (`transportation.sql:402`),
all service values z14. Links (ramp=1) from z9. Construction classes follow their base class, except
minor/service_construction z13 and path/track_construction z14. Railways: rail z8 (main line, `transportation.sql:455`),
narrow_gauge z10 (`:531`), light_rail (class transit) z11 (`:568`), rail service tracks z13, subway, tram, monorail,
funicular, preserved z14 (`:643`); aerialway z12 (`:681`); ferry z4 (`:752`); polygons (areas, piers, bridges) z13
(`:1112`). Attributes: brunnel from z7, ramp/layer/toll/access from z9, oneway/surface/service/level/indoor z12.

### Zooms

| Id | Rule | Ours MinZoom | Data | Style minzoom | Proposed |
|---|---|---|---|---|---|
| TR-1 | motorway | 4 | 4 | 5 (`:5961`) | 5 |
| TR-2 | trunk | 5 | 4 | 5 (`:5731`) | 5 |
| TR-3 | primary | 7 | 8 (port 7) | 7 (`:5514`) | 8 |
| TR-4 | secondary | 8 | 9 | 8 (`:5297`) | 9 |
| TR-5 | tertiary | 9 | 11 | 9 (`:5092`) | 11 |
| TR-6 | minor | 12 | 12 | 12 | 12 |
| TR-7 | service | 12 | 13 | 12 (`:4533`) | 13 |
| TR-8 | track | 12 | 12 (route members) | 12 | 12 |
| TR-9 | raceway | 9 | 12 | 9 (`:4717`) | 12 |
| TR-10 | busway / bus_guideway | 12 / 13 | 11 | not styled | 12 / 13 (unchanged) |
| TR-11 | path subclasses | 12 (corridor 14, platform 13) | 12 (route members), bridleway/corridor 14 in the scan | 12 (`:3842`–`4058`) | Unchanged |
| TR-12 | rail | 5 | 8 | none | 8 |
| TR-13 | transit/subway | 13 | 14 | none (`:6025`) | 14 |
| TR-14 | transit (light_rail, tram, monorail) | 12 | 11 (light_rail), 14 (tram, monorail) | none | 12 for transit; new rules transit/tram and transit/monorail at 14 |
| TR-15 | aerialway | 13 | 12 | 13 (`:8639`) | Unchanged |
| TR-16 | ferry | 6 | 4 | 6 (`:8854`) | Unchanged |
| TR-17 | pier line | 13 | 13 | none | Unchanged |
| TR-18 | Construction motorway / trunk / primary / secondary / tertiary | 5 / 7 / 7 / 8 / 9 | 4 / 4 / 8 / 9 / 11 | 5 / 7 / 7 / 8 / 9 | 5 / 7 / 8 / 9 / 11 |
| TR-19 | Construction minor / service / track / raceway / path | 12 / 12 / 12 / 12 / 13 | 13 / 13 / 14 / 12 / 14 | 12 / 12 / — / — / — | 13 / 13 / 14 / 12 / 14 |
| TR-20 | Areas pier, platform, pedestrian (fallback) / bridge | 0 / 11 | 13 | none / 11 (`:2266`–`2401`) | 13 for all |

### Widths and colors

Fit A (z12–z16); B for rules visible below z12 is in **Decision G-W**. "Casing" is per side.

| Id | Rule | Ours | Style (file `transportation/style.json`) | Proposed (A) |
|---|---|---|---|---|
| TR-21 | motorway | 22 m / 2.5 px, casing 0.75 m / 0.75 px | `Highway road` 0@4.9 → 1@7 → 25@18 base 1.2 (`:5961`); outline 1.75@7 → 27@18 (`:3370`) | 15 m / 8.8 px; casing 0.5 m / 0.6 px |
| TR-22 | trunk, primary | 18 m / 2.2 px, 16 m / 2 px | `Trunk road`, `Primary road` 4@12 → 8.6@15 → 16@16 (`:5731`, `:5514`); outlines (`:3190`, `:3280`) | 12.5 m / 4.9 px; casing 0.5 m / 0.7 px |
| TR-23 | secondary | 14 m / 1.6 px | 4.3@12 → 7.6@14 → 16@16 (`:5297`); outline (`:3105`) | 13.5 m / 5.7 px; casing 1 m / 0.5 px |
| TR-24 | tertiary | 12 m / 1.4 px | 3@12 → 7.8@14 → 16.4@16 (`:5092`); outline (`:3031`) | 15.5 m / 4.1 px; casing 0.5 m / 0.6 px |
| TR-25 | minor | 9 m / 1 px | 1@12 → 3.8@14 → 10.4@16 (`:4902`); outline from 13 (`:2949`) | 8.5 m / 0 px; casing 0.7 px, `CasingMinZoom` 13 |
| TR-26 | service | 5 m / 0.6 px, casing 0.5/0.5 | 0.9@13 → 2.3@15 → 5.4@16 (`:4533`); outline from 13 (`:2438`) | 3.5 m / 1.1 px; casing 0.5 m / 0.6 px, `CasingMinZoom` 13 |
| TR-27 | track | 3 m / 1 px, dash (3, 2.5), casing white α0.5 | 1.5@13 → 2@17 (`:4593`); outline α0.5 → 0.7, from 13 (`:2497`) | 0 m / 1.7 px; casing 0.5 m / 0.3 px, `CasingMinZoom` 13 |
| TR-28 | raceway | 8 m / 1 px | 1.9@12 → 3.9@13 → 5.1@14 → 11.5@16 (`:4717`) | 10 m / 2.9 px |
| TR-29 | path pedestrian | 6 m / 1 px, casing 0.5/0.5 | 1.9@13 → 3.8@14 → 10.4@16 (`:3498`); outline (`:3434`) | 8.5 m / 1.5 px; casing 0.5 m / 0.6 px |
| TR-30 | Links (`ramp=1`) | width × 0.6 at all zooms | Separate link layers: motorway link from 8, others from 11 (`:4138`–`4449`); fitted link widths are 0.63–0.9 of the road | `_rampWidth` 0.75; links other than motorway get item minimum zoom 11 (G-3) |
| TR-31 | steps | 2 m / 1.2 px, dash (0.5, 0.5), casing 0.6 px / 0.3 m | 1.7@12 → 2.3@18 (`:3915`); outline 4@14 → 4.6@18 α0.6, from 15 (`:3562`) | 0 m / 1.8 px; casing 1.1 px, `CasingMinZoom` 15 |
| TR-32 | cycleway, bridleway | 1.5 m / 0.7 px, casing 0.6 px | 0.7@12 → 1.3@18 (`:3842`, `:3980`); outlines 3@14 → 3.6@18, from 15 | 0 m / 0.8 px; casing 1.1 px, `CasingMinZoom` 15 |
| TR-33 | footway, path, fallback | 1.5 m / 0.6 px, dash (2, 1) | 0.6@13 → 1.3@15 → 1.6@18, filter subclass footway/path (`:4058`); outline 2.7@14 → 3.6@18, from 15 | 0 m / 0.9 px; casing 0.9 px, `CasingMinZoom` 15 |
| TR-34 | path corridor, platform lines | Own rules | Not styled (filters list footway/path) | Keep as extensions |
| TR-35 | rail | 3 m / 1 px; overlay white 0.5 of the width, dash (2.5, 2.5) of the width | `Major rail` 0.8@8 → 3@15 → 5.4@20 base 1.4, `hsl(0,0%,51%)` (`:6137`); hatching 0@9 → 1.5@14 → 3@20, dash (2.5, 2.5) of its own width (`:6250`) | 2.5 m / 2.3 px; overlay fraction 0.6, dash (1.5, 1.5) of the rail width |
| TR-36 | rail service siding/spur/yard | Like rail | `Major rail service` opacity 0.4 (`:6069`) | Alpha 0.4 for `service` siding/spur/yard (transportation job) |
| TR-37 | transit (tram, light_rail) | 1.5 m / 0.8 px, overlay `hsl(0,0%,73%)` 1.6, dash (0.2, 4) | `Minor rail` 0.8@12 → 1.2@15 → 4@20 (`:6204`); hatching 2@15 → 6@20, dash (0.2, 4) of its width, from 14.5 (`:6300`) | 1 m / 1 px; overlay fraction 1.6, dash (0.32, 6.4) |
| TR-38 | transit/subway | 2 m / 0.8 px, dash (1, 1.2) | 1@14 → 3@18 (`:6025`) | 1.5 m / 1.2 px |
| TR-39 | aerialway | 1.5 m / 1 px; overlay `hsl(0,0%,26%)` 1.2, dash (0.5, 10) | `Cablecar` 1@11 → 2.5@19; dash layer 1@11 → 3@19, dash (0.5, 10) (`:8639`, `:8672`) | 1.5 m / 1.5 px; overlay fraction 1.1, dash (0.55, 11) |
| TR-40 | ferry | 2 m / 0.8 px | 0.5@10 → 1.1@14, opacity 0@5 → 1@9 (`:8854`) | 0 m / 0.9 px |
| TR-41 | pier line | 3 m / 1 px | 1@15 → 4@17 (`:2294`) | 1.5 m / 0.9 px |
| TR-42 | Construction (motorway … minor) | 14 / 12 / 11 / 10 / 8 / 6 m, 1 px; white dash overlay | Base 1@11 → 2@12 → 4@13 → 7@16, white dash (1, 1) of the same width (`:5835`–`4834`) | 7.5 m / 3.1 px for all; overlay unchanged |
| TR-43 | service_construction | `hsl(0,0%,83%)` + white dashes | `Service road under construction`: white, dash (1, 1), service widths (`:4655`) | White, dash (1, 1), 3.5 m / 1.1 px, no overlay |
| TR-44 | track/raceway/path construction | Own | Not styled | Keep |
| TR-45 | Tunnels | Fill mixed 45 % toward white, casing dash (1, 0.5) | Lighter fills (e.g. `hsl(349,65%,84%)` for motorway), casing dash (0.5, 0.25) (`:2045`, `:658`) | Casing dash (0.5, 0.25); fill mix unchanged |
| TR-46 | Bridge casing | Rule casing mixed 40 % toward black; `hsl(0,0%,35%)` without casing | Black for motorway, primary, secondary, tertiary, paths (`:6927`, `:6870`, `:6697`, `:6571`); trunk keeps its casing color (`:6802`); service `hsl(36,5%,80%)` (`:6416`) | Black, except trunk (own casing) and service (`hsl(36,5%,80%)`) |
| TR-47 | Areas | Colors match | `Pier`, `Bridge area`, `Platform area` (+ outline `hsl(0,0%,50%)`), `Pedestrian area` | Unchanged (fill outlines not reproduced) |

### Attributes

| Id | Attribute | Ours | Reference | Proposed |
|---|---|---|---|---|
| TR-48 | brunnel | tunnel/bridge; `ford` ignored | `transportation.yaml`: bridge, tunnel, ford; style ignores ford | Unchanged |
| TR-49 | surface=unpaved | Tint 0.25 toward dirt | Not styled | Default 0 (option kept) |
| TR-50 | access=no | Fade 0.3 | Not styled | Default 0 (option kept) |
| TR-51 | service driveway/parking_aisle/drive-through | Width × 0.6 | Not styled; `drive-through` is not a schema value (`class.sql`, `service_value`) | Default 1; drop `drive-through` |
| TR-52 | toll, expressway, network, mtb_scale, foot, official | Toll tint 0, others unused | Not styled | Unchanged |
| TR-53 | layer, level, indoor, bicycle/horse=designated, oneway | As yaml | As yaml | Unchanged |

### Oneway arrows

| Id | Item | Ours (`OmtLabelDefaults.cs:231`) | Reference | Proposed |
|---|---|---|---|---|
| TR-54 | MinZoom | path 16, roads 15, minor 15.5, service 16 | All 15 (`transportation/style.json:8709`, `:8753`) | 15 |
| TR-55 | Spacing | 95 for all | roads 95, path 125 | path 125 (separate pass) |
| TR-56 | `oneway=-1` | Arrow reversed | `Oneway opposite`: icon rotated −90°, opacity 0.5, size 0.5@15, spacing 75 (`:8803`); `Oneway path` only `oneway=1` | Keep reversal (matches `transportation.yaml`); −90° and opacity not reproduced; path `-1` not drawn |

## building

Data: `building.sql:85` — z13 merged blocks without heights, z14 all buildings with `render_height =
ceil(height, levels · 3.66, 5)` and `render_min_height` (`:101`). `colour` is the OSM `building:colour` value or a hex
color derived from `building:material` (`:62`). `hide_3d` is `true` or absent.

| Id | Item | Ours | Reference | Proposed |
|---|---|---|---|---|
| BL-1 | `colour` | Hex only (`BuildingPropertiesJob.cs:117`) | Raw OSM values: CSS names (`beige`, `brown`, `firebrick`) and lists (`beige;brown`) in the scan | Accept CSS named colors and use the first value of a `;` list |
| BL-2 | Default height | 6 m (`OmtBuildingHandler.cs:31`) | 5 m at z14 (`building.sql:101`); z13 blocks have none | 5 m |
| BL-3 | Color | `hsl(26,17%,82%)` | `Building` `hsl(24,19%,84%)`@13 → `hsl(26,17%,82%)`@16 (`building/style.json:4`) | Unchanged |
| BL-4 | hide_3d, render_height, render_min_height | As yaml | As yaml | Unchanged |

## water_name

Data: `water_name.sql:4` — lake center lines z3–z13 when the label fits, all at z14 (`:34`); lake points by area
from z3, all at z14 (`:55`); marine points from their rank, bays from z13, others from z8 (`:76`). Scan: lake points and
lines from z7, bay z13.

| Id | Item | Ours (`OmtLabelDefaults.cs:138`) | Reference | Proposed |
|---|---|---|---|---|
| WN-1 | **Decision**: point labels | ocean 0, sea 3, strait 8, lake 9, bay 13, fallback 12; ocean/sea italic 16/13 | `Water labels`: all points except ocean, minzoom **15**, size 11, regular, max width 5 (`water_name/style.json:47`); ocean not labeled | Exact: ocean hidden, others 15, size 11, regular. Alternative: exact for lake and bay, but keep our zooms for ocean, sea and strait, which name large areas seen from far away. I recommend the alternative |
| WN-2 | Line labels | 3, size 12, `hsl(238,43%,55%)`, halo 0.7 | `Lakeline labels` minzoom 0 (`:4`) | Unchanged |

## transportation_name

Data: `transportation_name.sql:4` — motorway z6 (`:121`), motorway z7 (`:143`), motorway/trunk z8–z11 (`:165`, `:189`;
`update_transportation_name.sql:463`), all non-link roads except minor and paths z12 (`:205`), minor z12, paths z13
(`:229`), all z14 (`:257`); motorway junctions from z10 (`:285`). Scan: trunk z8, primary…minor z12, service z13, track
z14, junction points z10 (class = the highest crossing road class).

| Id | Item | Ours (`OmtLabelDefaults.cs:184`) | Reference | Proposed |
|---|---|---|---|---|
| TN-1 | **Decision**: name MinZoom | motorway/trunk 11, primary 12, secondary 12.5, tertiary 13, minor 14, service/path/track 15.5, rail/transit 15 | `Road labels`: all classes except ferry from 11 (`transportation_name/style.json:63`) | Exact: max(11, data): motorway/trunk 11, primary…minor, service, track, rail, transit 12, path 13. Alternative: keep our staggered zooms (allowed: later than the style) |
| TN-2 | Name size, spacing | 12/11/10, spacing 300 | 9@14 → 13@18, spacing 250 (<17) | 9, spacing 250 |
| TN-3 | Ferry | 14, size 11 | `Ferry labels` 14, 10@14 → 12@18, halo `hsl(195,45%,77%)` 2 (`:4`) | Size 10 |
| TN-4 | Shield MinZoom | motorway 9, trunk 9, primary 10, secondary 11, tertiary 12 | All 9 (`:135`–`451`); data motorway 6, trunk 8, primary/secondary/tertiary 12 | 9, 9, 12, 12, 12 (primary and secondary were below data) |
| TN-5 | Shield size, spacing | 10, spacing 500 | 10@9 → 11@15 → 12@17; spacing 560, motorway 760 | 11; spacing 560 (motorway 760, separate rule pass) |
| TN-6 | Junctions | `ref`/`name`, 14 | Not styled | Keep as extension |

## place

Data: `place.sql:4` — continents z0–z3 (`:36`), countries `rank <= z + 1` (`:57`), states from z2 (`:80`), island
points from z12 (`:97`), island polygons from z8 by area (`:117`), aboriginal lands from z6 by area (`:138`); cities
`city.sql:33`: Natural Earth ranked city/town/village from z2 (`rank <= z + 1` up to z7), grid places: town z7
(`:65`), village z8–z10, hamlet/borough/suburb z11–z13, all z14. `capital` is 2–6 (`capital.sql`); `islet` is not a
class (island points get `island`). Scan: capital 2, 4, 6; no `islet`, no capital 1.

| Id | Item | Ours (`OmtLabelDefaults.cs:28`) | Reference | Proposed |
|---|---|---|---|---|
| PL-1 | `islet` rule | Present | Not a schema class | Remove |
| PL-2 | City with capital 1 | Rule present | `capital` 2–6 (`place.yaml`); the style's `in capital 1, 2` matches 2 only | Remove the "1" rule |
| PL-3 | Country | size 15, `hsl(302,16%,36%)`, halo 1 | `Country labels` 2–12, size 17 (rank > 2) / 19 at z7+, color `hsl(302,16%,36%)`@2 → `hsl(273,33%,22%)`@10, halo 0.8, filter `iso_a2 != VA` (`place/style.json:499`) | Size 17, color `hsl(273,33%,22%)`, halo 0.8; skip `iso_a2=VA` (modifier) |
| PL-4 | State, province | 4–12, size 12, halo 0.8 | `State labels` 4–12, `class = state`, `rank < 3`, size 10@3 → 14@6, regular, halo 0.8 (`:236`) | Size 14, `MaxRank 2` (G-4); province keeps the state style (not styled) |
| PL-5 | Capital city | 3, no maxzoom, size 16, star icon at all zooms | `Capital city labels` 3–15, size 12@4 → 18@12, color 29 % → 13 %, halo 1.2, star icon only below z8 (`:406`) | MaxZoom 15, size 18; icon kept (Limitations) |
| PL-6 | City | 4, no maxzoom, size 14, `hsl(0,0%,20%)`, dot icon | `City labels` 4–14, size 11@4 → 16@15 (14.2 at z13), color 35 % → 13 %, dot icon only below z7 (`:304`) | MaxZoom 14, size 14, color `hsl(0,0%,15%)` (z13); drop the dot (the style shows it only on z4–z6) |
| PL-7 | Town | 7, size 13 | `Town labels` minzoom 6, size 10@7 → 13@11, anchor bottom (`:170`) | Unchanged (7 = data) |
| PL-8 | Village | 9, size 12 | `Village labels` 8, size 10@10 → 16@15 (14.3 at z14), halo 1.2 (`:109`) | 8 (data), size 14 |
| PL-9 | Other labels: suburb, hamlet, neighbourhood, island | 11 / 12 / 14 / 8; sizes 12 / 11 / 11 / 12; colors 40 % / 27 % / 47 % / 27 %; island italic | `Other labels` minzoom 8, size 10@11 → 14@14, color `hsl(0,0%,47%)` from 12.6, halo white 1.5, semibold, max width 10, spacing 150 (`:4`) | 11 / 11 / 14 / 8 (data); size 14; color 47 %; bold; island not italic; max width 10 |
| PL-10 | borough, quarter, isolated_dwelling, aboriginal_lands | Own | Not styled | Keep, colors as Other labels |
| PL-11 | Fonts | country bold, others regular | Semibold (country, capital, city, town, village, other), state regular | Bold for semibold layers (see Fonts) |

## housenumber

Data: z14 only (`housenumber.sql:26`).

| Id | Item | Ours | Reference | Proposed |
|---|---|---|---|---|
| HN-1 | Size | 10 | 9@17 → 11@22, minzoom 17, padding 3 (`housenumber/style.json:4`) | 9 |

## poi

Data: `poi.sql:46` — points z12–z13 only railway stations, halts and ferry terminals (`:54`), all from z14 (`:65`);
polygons z10–z13 universities/colleges larger than 10 % of a tile (`:79`). `agg_stop` (`update_poi_point.sql:66`) is
1 on every stop of subclass bus_stop, bus_station, tram_stop and subway, except the non-main stops of a `uic_ref` group,
where it is absent. Scan: railway (station, halt) z12, tram_stop/subway z14.

| Id | Item | Ours (`OmtLabelDefaults.cs:54`, `OmtPoiHandler.cs:48`) | Reference | Proposed |
|---|---|---|---|---|
| POI-1 | `agg_stop` | Skips stops whose `agg_stop` is present and ≠ 1, which never happens: grouped stops are all labeled | Grouped non-main stops have no `agg_stop` | Skip stops of the four subclasses without `agg_stop` (G-3) |
| POI-2 | railway tram_stop, subway | railway 12 | data 14 | Rules railway/tram_stop and railway/subway at 14 |
| POI-3 | Shop group | 15 | `Shop` 15, but text/icon opacity 0 below 17 except supermarket (`poi/style.json:4`) | 17; supermarket 15 |
| POI-4 | Health | 16 for all | `Health` 16; opacity: hospital 14+, pharmacy 16+, others 17+ (`:1067`) | hospital, pharmacy 16; dentist, veterinary 17 |
| POI-5 | Sizes | 11 (mall 12, harbor 13) | 10–10.6 at the group's minzoom; fixed 11 for Mortuary, Education, Train, Bus station; Mall 12, Zoo 12, Harbor 14 | 10 (Waste, Transport, Accommodation, Bus stop 10.5), fixed 11 groups 11, Mall 12, Zoo 12, Harbor 14 |
| POI-6 | Fonts | Regular | Semibold (most groups), Bold (Train, Bus station, Zoo), Semibold Italic (Sport, Education, Harbor, Mall, Local park), Bold Italic (Mortuary), Regular (Ferry, Health, Bus stop) | Bold / Bold / Italic / Italic / Regular |
| POI-7 | Halo widths | 1 | 0.8 (Mortuary, Education, Bus stop, Bus station, Train), 0.2 (Sport), 0.3 (Harbor), 1 otherwise | As the style |
| POI-8 | Max width | 8 | 9 (14 for Mortuary, Bus stop, Bus station, Train; 6 for Harbor, Zoo; 5–8 for Local park) | As the style |
| POI-9 | Public excludes subclass books; Cultural excludes books, musical_instrument, art; Transport excludes car_parts | `books`, `car_parts` matched first as Shop | Same filters | Unchanged (first-match order already implements them); Cultural exclusions added as hidden rules before Cultural |
| POI-10 | Classes in rules that the schema never writes: `gallery`, `arts_centre` (both become class `art_gallery`) | Rules present | `poi.yaml` class `art_gallery` | Remove (the style lists them, but they match nothing) |
| POI-11 | Icon fallback `dot` | — | `Shop` icon `coalesce(subclass, class, "dot")` | Not in our atlas: no icon (unchanged) |

## aerodrome_label

Data: `aerodrome_label.sql:40` — z8–z9 international airports with IATA, z10+ all (`:59`).

| Id | Item | Ours (`OmtLabelDefaults.cs:121`, `OmtAerodromeLabelHandler.cs:27`) | Reference | Proposed |
|---|---|---|---|---|
| AD-1 | Filter | All airports | `Major airport labels`: `has iata` only, 8–17 (`aerodrome_label/style.json:4`) | Require `iata`; MaxZoom 17 |
| AD-2 | **Decision**: MinZoom | international 8, others 10–11.75 | Icon from 8, name from 11 (`text-field` stops 8:" ", 11:"{name}") | 11 for all (text zoom; icon-only stage in Limitations), or keep 8/10 with the name shown early |
| AD-3 | Text | name + IATA code (`SecondLine.Code`) | `{name}` only | Default `SecondLine.None` (option kept) |
| AD-4 | Size | 12 / 11 | 10@8 → 12@14 | 12 for all |

## Name keys

The default `OmtTextSettings.NameKeys` (`name:en`, `name`) is the style's `coalesce(name:en, name)`. The schema also
writes `name:latin`, `name:nonlatin`, `name_int` and the deprecated `name_en`/`name_de` (scan: all layers with names);
they stay available through the preference list. The style's country and capital labels use `{name:en}` without a
fallback; we fall back to `name` (deliberate).

## Port differences (planetiler vs SQL)

| Layer | Port (scan) | SQL | Used |
|---|---|---|---|
| transportation | primary from z7 | z8 (`update_transportation_merge.sql:823`, `:1452`) | z8 |
| boundary | admin 5–6 from z9, admin 8 from z11, admin 9–10 at z12 | z10, z12, z13 (`boundary.sql:73`–`113`) | SQL |
| landuse | Classes outside the schema (allotments, autodrome, construction, driving_school, education, exhibition_centre, farmyard, fountain, grass, greenfield, healthcare, nordic_park, park, recreation_ground, resort, shelter, stables) | `landuse.yaml` values | Not drawn |
| transportation_name | service names z13, track names z14 | z12 (`transportation_name.sql:205`) | Data zoom 12 |
| water_name | lake points and lines from z7 | from z3 by area / label length | Data zoom 3 |

## Out of scope (recorded)

- Placement order of labels between layers: tile layer order (alphabetical on this source) instead of the style
  order (see [Label priorities](#label-priorities)); needs a Core change of `LayerLoadContext`/`ILayerHandler`.
- Label collisions across tiles.

## Not reproducible (Limitations)

Fill and line patterns (landcover, landuse, garden); fill opacity ramps and `maxzoom` of fills (medium-scale land
cover up to z13); `line-offset` (park and military outlines, theme parks); zoom-interpolated colors, widths and text
sizes (one value per rule, see the conventions above); text that appears later than its icon (`text-field` zoom stops:
airports, peaks, city dots, capital stars); `text-variable-anchor`, `text-line-height`, `icon-text-fit-padding`;
icon halos and icon opacity; the `−90°` rotation of `Oneway opposite`; zoom-dependent minimum pixel widths below z12
(see G-W); with G-1 A, fill rules whose zoom is above the source's deepest zoom (fills are gated by tile zoom).

## Tests to add

- `SchemaZooms` (`Tests/EditMode/OpenMapTiles/OmtCommonTests.cs`) extended to fills and lines of all layers with the
  data zooms above.
- Value coverage: every schema value (`*.yaml` `values`) of class/subclass of water, waterway, landcover, landuse,
  aeroway, transportation, transportation_name, place, water_name, mountain_peak, aerodrome_label matches a visible or
  deliberately hidden rule; rules for values the schema never writes fail.
- Synthetic tiles (`OmtTestTile`): `agg_stop`, rail service opacity, ramp minimum zoom, waterway tunnel zoom, state
  `MaxRank`, `iso_a2=VA`, aerodrome without IATA, peak without `ele`, a line rule with `MinZoom` above the tile zoom
  is still built with that minimum zoom (tiles of z12 and z14), `CasingMinZoom`, building named colors.
