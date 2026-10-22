using System.Collections.Generic;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Lays out and places the labels of one tile layer and emits their quads for the label shader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Labels are screen-space billboards of constant pixel size: every vertex stores the label anchor (on the
    /// ground, lifted by <see cref="Lift"/>) plus a pixel offset; line labels also store the line tangent so the shader
    /// rotates them to follow the line on screen.
    /// </para>
    /// <para>
    /// Decluttering is precomputed per tile on display-zoom tiers <see cref="FirstTier"/>..<see cref="LastTier"/>:
    /// tier <c>t</c> is the tile viewed at zoom <c>TileZoom + t</c>, where the tile is <c>512 · 2^t</c> pixels wide
    /// (negative tiers for tiles shown smaller than 512 pixels, see <see cref="OmtZoom.LabelTierRange"/>). Requests are
    /// placed greedily in priority order; each label gets the lowest tier from which it collides with no
    /// higher-priority label on any deeper tier, and that tier becomes its minimum display zoom, at which the shader
    /// fades it in. So the labels match the size at which the tile is actually shown, and tiles of the source's maximum
    /// zoom, which are viewed at much deeper zooms, show progressively more labels as the camera approaches.
    /// The placed boxes go to <see cref="Occupancy"/>, which is shared by all label layers of the tile (they are built
    /// one after another), so labels of different layers avoid each other too. Collisions across tile borders are not
    /// resolved.
    /// </para>
    /// </remarks>
    [BurstCompile]
    internal struct OmtLabelLayoutJob : IJob
    {
        private const float TilePixels = 512f;
        private const float CellSize = 64f;
        private const float MaxBendRadians = 0.6f;

        [ReadOnly] public NativeArray<OmtLabelRequest> Requests;
        [ReadOnly] public NativeArray<OmtLabelRuleData> Rules;
        [ReadOnly] public NativeHashMap<int, OmtGlyphInfo> Glyphs;
        [ReadOnly] public NativeArray<OmtFontMetrics> Metrics;
        [ReadOnly] public NativeArray<OmtIconInfo> Icons;

        [ReadOnly] public NativeArray<MvtGeomType> GeomTypes;
        [ReadOnly] public NativeArray<int2> FeatureRingRange;
        [ReadOnly] public NativeArray<int2> Rings;
        [ReadOnly] public NativeArray<int2> RingPoints;
        public TileCoord Coord;
        public uint Extent;
        [ReadOnly] public GroundGrid Grid;
        public MercatorPivot LocalFrame;

        public float Lift;
        public float TileZoom;
        /// <summary>The lowest declutter tier (relative to the tile zoom, may be negative).</summary>
        public int FirstTier;
        /// <summary>The highest declutter tier; labels placed on it stay visible at deeper zooms.</summary>
        public int LastTier;
        public float Padding;

        /// <summary>
        /// Boxes placed so far on the tile by all label layers, keyed by tier and cell (see <see cref="CellKey"/>).
        /// Read for collisions; written only when <see cref="Occupy"/> is set.
        /// </summary>
        public NativeParallelMultiHashMap<int, float4> Occupancy;

        /// <summary>
        /// Whether the placed labels block later layers. Low-priority layers (house numbers) avoid the others without
        /// blocking them; their own labels still avoid each other.
        /// </summary>
        public bool Occupy;

        public NativeList<float3> Positions;
        /// <summary>Per-vertex float attributes, parallel to <see cref="Positions"/>.</summary>
        public NativeList<OmtLabelVertex> Vertices;
        /// <summary>Per-vertex text and halo colors, parallel to <see cref="Positions"/>.</summary>
        public NativeList<OmtLabelColors> Colors;
        /// <summary>Indices grouped by texture slot, in the order of <see cref="SubMeshes"/>.</summary>
        public NativeList<int> Indices;
        /// <summary>(slot, first index, index count) per submesh.</summary>
        public NativeList<int3> SubMeshes;
        /// <summary>[0] = min, [1] = max of the anchors.</summary>
        public NativeArray<float3> Bounds;

        private struct Quad
        {
            public float4 Rect;  // px relative to the label origin, y up
            public float4 Uv;
            public int Slot;
            public bool Icon;
        }

        private struct Layout
        {
            public int QuadStart;
            public int QuadCount;
            public float4 Box;   // px relative to the label origin
        }

        private struct Candidate
        {
            public int Request;
            public float2 TilePx;   // tier-0 tile pixels, y up
            public float3 Anchor;
            public float3 Tangent;
            public float Angle;     // tangent angle in tile space (for the collision box)
            public int MinTier;
            public int Order;
        }

        private struct CandidateComparer : IComparer<Candidate>
        {
            public NativeArray<OmtLabelRequest> Requests;
            public NativeArray<OmtLabelRuleData> Rules;

            public int Compare(Candidate a, Candidate b)
            {
                var ra = Requests[a.Request];
                var rb = Requests[b.Request];
                int c = Rules[ra.Rule].Priority.CompareTo(Rules[rb.Rule].Priority);
                if (c != 0) return c;
                c = ra.Priority.CompareTo(rb.Priority);
                if (c != 0) return c;
                c = ra.Key.CompareTo(rb.Key);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            }
        }

        public void Execute()
        {
            var quads = new NativeList<Quad>(256, Allocator.Temp);
            var layouts = new NativeArray<Layout>(Requests.Length, Allocator.Temp);
            var candidates = new NativeList<Candidate>(Requests.Length, Allocator.Temp);
            var codepoints = new NativeList<int>(64, Allocator.Temp);
            var lineBreaks = new NativeList<int2>(4, Allocator.Temp);
            var lineWidths = new NativeList<float>(4, Allocator.Temp);
            var linePx = new NativeList<float2>(64, Allocator.Temp);
            var lineLen = new NativeList<float>(64, Allocator.Temp);
            var own = new NativeParallelMultiHashMap<int, float4>(64, Allocator.Temp);
            var quadSlots = new NativeList<int>(256, Allocator.Temp);
            var fits = new NativeArray<bool>(LastTier - FirstTier + 1, Allocator.Temp);

            Bounds[0] = new float3(float.MaxValue);
            Bounds[1] = new float3(float.MinValue);

            for (int r = 0; r < Requests.Length; r++)
            {
                layouts[r] = LayoutRequest(Requests[r], ref quads, ref codepoints, ref lineBreaks, ref lineWidths);
                if (layouts[r].QuadCount == 0) continue;
                CollectCandidates(r, layouts[r], ref candidates, ref linePx, ref lineLen);
            }

            candidates.Sort(new CandidateComparer { Requests = Requests, Rules = Rules });

            for (int c = 0; c < candidates.Length; c++)
            {
                var cand = candidates[c];
                var req = Requests[cand.Request];
                var rule = Rules[req.Rule];
                var layout = layouts[cand.Request];
                float4 box = RotatedBox(layout.Box, cand.Angle) + new float4(-Padding, -Padding, Padding, Padding);
                bool overlap = (req.Flags & OmtLabelFlags.AllowOverlap) != 0;

                // A label is only tested (and only blocks others) on the tiers where it can be visible: below its
                // style's min zoom it is faded out anyway and must not take space from visible labels.
                float styleMinZoom = math.max(rule.MinZoom, req.MinZoom);
                int styleTier = math.clamp((int)math.ceil(styleMinZoom - TileZoom - 1e-4f), FirstTier, LastTier);
                int floorTier = math.max(cand.MinTier, styleTier);
                int minTier = floorTier;
                if (!overlap)
                {
                    for (int t = FirstTier; t <= LastTier; t++)
                        fits[t - FirstTier] = t >= floorTier && Fits(ref Occupancy, t, cand.TilePx, box) &&
                                              Fits(ref own, t, cand.TilePx, box);
                    minTier = int.MinValue;
                    for (int t = LastTier; t >= floorTier && fits[t - FirstTier]; t--) minTier = t;
                    if (minTier == int.MinValue) continue;
                }

                float minZoom = math.max(styleMinZoom, TileZoom + minTier);
                float maxZoom = math.min(rule.MaxZoom, req.MaxZoom);
                if (minZoom >= maxZoom) continue;

                if (!overlap)
                    for (int t = minTier; t <= LastTier; t++)
                    {
                        if (Occupy) Insert(ref Occupancy, t, cand.TilePx, box);
                        else Insert(ref own, t, cand.TilePx, box);
                    }

                Emit(cand, req, rule, layout, quads, minZoom, maxZoom, ref quadSlots);
            }

            BuildIndices(quadSlots);

            quads.Dispose();
            layouts.Dispose();
            candidates.Dispose();
            codepoints.Dispose();
            lineBreaks.Dispose();
            lineWidths.Dispose();
            linePx.Dispose();
            lineLen.Dispose();
            own.Dispose();
            quadSlots.Dispose();
            fits.Dispose();
        }

        // ---- Text and icon layout ----------------------------------------------

        private Layout LayoutRequest(OmtLabelRequest req, ref NativeList<Quad> quads, ref NativeList<int> codepoints,
            ref NativeList<int2> lineBreaks, ref NativeList<float> lineWidths)
        {
            var layout = new Layout { QuadStart = quads.Length };
            var rule = Rules[req.Rule];
            float size = rule.Size;
            var metrics = Metrics[(int)rule.Font];
            float lineH = (metrics.LineHeight > 0f ? metrics.LineHeight : 1.2f) * size;
            float ascent = (metrics.Ascent > 0f ? metrics.Ascent : 0.9f) * size;
            float space = (metrics.SpaceAdvance > 0f ? metrics.SpaceAdvance : 0.25f) * size;
            float spacing = rule.LetterSpacing * size;

            codepoints.Clear();
            OmtText.Decode(req.Text, rule.Transform, ref codepoints);

            // Greedy word wrap (point labels only); '\n' always breaks.
            lineBreaks.Clear();
            lineWidths.Clear();
            float maxWidth = req.Kind == OmtLabelKind.Point && rule.MaxWidth > 0f ? rule.MaxWidth * size : float.MaxValue;
            int lineStart = 0, lastSpace = -1;
            float width = 0f, widthAtSpace = 0f;
            for (int i = 0; i < codepoints.Length; i++)
            {
                int cp = codepoints[i];
                if (cp == '\n')
                {
                    lineBreaks.Add(new int2(lineStart, i));
                    lineWidths.Add(width);
                    lineStart = i + 1;
                    lastSpace = -1;
                    width = 0f;
                    continue;
                }
                float adv = Advance(rule.Font, cp, space, size) + spacing;
                if (cp == ' ')
                {
                    lastSpace = i;
                    widthAtSpace = width;
                }
                width += adv;
                if (width > maxWidth && lastSpace > lineStart)
                {
                    lineBreaks.Add(new int2(lineStart, lastSpace));
                    lineWidths.Add(widthAtSpace);
                    lineStart = lastSpace + 1;
                    width -= widthAtSpace + Advance(rule.Font, ' ', space, size) + spacing;
                    lastSpace = -1;
                }
            }
            if (lineStart < codepoints.Length)
            {
                lineBreaks.Add(new int2(lineStart, codepoints.Length));
                lineWidths.Add(width);
            }

            float blockW = 0f;
            for (int i = 0; i < lineWidths.Length; i++) blockW = math.max(blockW, lineWidths[i]);
            float blockH = lineWidths.Length * lineH;
            bool hasText = lineWidths.Length > 0 && blockW > 0f;

            // Icon size and the text block position relative to the anchor.
            bool hasIcon = req.Icon >= 0 && req.Icon < Icons.Length && req.IconMode != OmtIconMode.None;
            float2 iconSize = hasIcon ? Icons[req.Icon].Size * rule.IconScale : float2.zero;
            float gap = rule.TextOffset * size;
            float blockTop;
            switch (req.IconMode == OmtIconMode.Shield || req.Kind == OmtLabelKind.Line ? OmtTextAnchor.Center : rule.Anchor)
            {
                case OmtTextAnchor.Top:
                    blockTop = -(iconSize.y * 0.5f + (hasIcon ? gap : 0f));
                    break;
                case OmtTextAnchor.Bottom:
                    blockTop = iconSize.y * 0.5f + (hasIcon ? gap : 0f) + blockH;
                    break;
                default:
                    blockTop = blockH * 0.5f;
                    break;
            }
            float blockLeft = -blockW * 0.5f;
            float4 box = new float4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);

            if (hasIcon)
            {
                var icon = Icons[req.Icon];
                float4 rect;
                if (req.IconMode == OmtIconMode.Shield)
                {
                    const float padX = 4f, padY = 3f;
                    float w = math.max(blockW + 2f * padX, iconSize.x);
                    float h = math.max(blockH + 2f * padY, iconSize.y);
                    rect = new float4(-w * 0.5f, -h * 0.5f, w * 0.5f, h * 0.5f);
                }
                else
                {
                    rect = new float4(-iconSize * 0.5f, iconSize * 0.5f);
                }
                quads.Add(new Quad { Rect = rect, Uv = icon.Uv, Slot = 0, Icon = true });
                box = Union(box, rect);
            }

            if (hasText)
            {
                for (int li = 0; li < lineBreaks.Length; li++)
                {
                    float pen = blockLeft + (blockW - lineWidths[li]) * 0.5f;
                    float baseline = blockTop - ascent - li * lineH;
                    int2 range = lineBreaks[li];
                    for (int i = range.x; i < range.y; i++)
                    {
                        int cp = codepoints[i];
                        if (Glyphs.TryGetValue(OmtText.GlyphKey(rule.Font, cp), out var g))
                        {
                            if (g.Slot > 0)
                            {
                                var rect = new float4(pen + g.Quad.x * size, baseline + g.Quad.y * size,
                                    pen + g.Quad.z * size, baseline + g.Quad.w * size);
                                quads.Add(new Quad { Rect = rect, Uv = g.Uv, Slot = g.Slot, Icon = false });
                            }
                            pen += g.Advance * size + spacing;
                        }
                        else
                        {
                            pen += (cp == ' ' ? space : 0.5f * size) + spacing;
                        }
                    }
                }
                box = Union(box, new float4(blockLeft, blockTop - blockH, blockLeft + blockW, blockTop));
            }

            layout.QuadCount = quads.Length - layout.QuadStart;
            layout.Box = layout.QuadCount > 0 ? box : float4.zero;
            return layout;
        }

        private float Advance(OmtFontStyle style, int cp, float space, float size)
        {
            if (Glyphs.TryGetValue(OmtText.GlyphKey(style, cp), out var g)) return g.Advance * size;
            return cp == ' ' ? space : 0.5f * size;
        }

        private static float4 Union(float4 a, float4 b) => new float4(math.min(a.xy, b.xy), math.max(a.zw, b.zw));

        // ---- Placement candidates ----------------------------------------------

        private void CollectCandidates(int r, Layout layout, ref NativeList<Candidate> candidates,
            ref NativeList<float2> linePx, ref NativeList<float> lineLen)
        {
            var req = Requests[r];
            int fi = req.FeatureIndex;
            if ((uint)fi >= (uint)GeomTypes.Length) return;
            int2 ringRange = FeatureRingRange[fi];

            if (req.Kind == OmtLabelKind.Point)
            {
                if (GeomTypes[fi] != MvtGeomType.Point) return;
                for (int k = 0; k < ringRange.y; k++)
                {
                    int2 ring = Rings[ringRange.x + k];
                    for (int p = 0; p < ring.y; p++)
                    {
                        if (!GroundAnchors.TryProjectPoint(RingPoints[ring.x + p], Coord, Extent, Grid, LocalFrame,
                                out float3 pos, out float2 uv)) continue;
                        candidates.Add(new Candidate
                        {
                            Request = r,
                            TilePx = uv * TilePixels,
                            Anchor = pos + new float3(0f, Lift, 0f),
                            Angle = 0f,
                            MinTier = FirstTier,
                            Order = candidates.Length,
                        });
                    }
                }
                return;
            }

            if (GeomTypes[fi] != MvtGeomType.LineString) return;
            bool iconOnly = (req.Flags & OmtLabelFlags.AllowOverlap) != 0;
            float labelLen = math.max(1f, layout.Box.z - layout.Box.x);
            float spacing = math.max(labelLen * 1.5f, req.Spacing);
            double4 bounds = Coord.BoundsMeters();

            for (int k = 0; k < ringRange.y; k++)
            {
                int2 ring = Rings[ringRange.x + k];
                if (ring.y < 2) continue;

                linePx.Clear();
                lineLen.Clear();
                float total = 0f;
                for (int p = 0; p < ring.y; p++)
                {
                    double2 m = MvtTileCoords.ToMercator(RingPoints[ring.x + p], bounds, Extent);
                    float2 px = GroundAnchors.ToTileUv(m, bounds) * TilePixels;
                    if (linePx.Length > 0)
                    {
                        float d = math.distance(linePx[linePx.Length - 1], px);
                        if (d < 1e-4f) continue;
                        total += d;
                    }
                    linePx.Add(px);
                    lineLen.Add(total);
                }
                if (linePx.Length < 2 || total <= 0f) continue;

                if (iconOnly)
                {
                    for (float s = spacing * 0.5f; s < total; s += spacing)
                        AddLineCandidate(r, s, 0f, FirstTier, false, ref candidates, linePx, lineLen);
                    continue;
                }

                // The shortest tier at which the label fits the line at all.
                int floorTier = FirstTier;
                while (floorTier <= LastTier && total < labelLen * math.exp2(-floorTier)) floorTier++;
                if (floorTier > LastTier) continue;

                if (total < spacing)
                {
                    AddLineCandidate(r, total * 0.5f, labelLen, floorTier, true, ref candidates, linePx, lineLen);
                }
                else
                {
                    for (float s = spacing * 0.5f; s < total; s += spacing)
                        AddLineCandidate(r, s, labelLen, floorTier, true, ref candidates, linePx, lineLen);
                }
            }
        }

        /// <summary>
        /// Adds a line candidate centered at distance <paramref name="s"/> along the line. With
        /// <paramref name="checkStraight"/>, the span the label covers must not bend more than
        /// <see cref="MaxBendRadians"/>; deeper tiers (shorter spans in tile space) are tried if it does, and the
        /// tier found becomes the candidate's minimum tier.
        /// </summary>
        private void AddLineCandidate(int r, float s, float labelLen, int floorTier, bool checkStraight,
            ref NativeList<Candidate> candidates, NativeList<float2> pts, NativeList<float> len)
        {
            float total = len[len.Length - 1];
            int tier = floorTier;
            float2 a = default, b = default;
            bool ok = false;
            for (; tier <= LastTier; tier++)
            {
                float half = labelLen * math.exp2(-tier) * 0.5f;
                float center = math.clamp(s, math.min(half, total * 0.5f), math.max(total - half, total * 0.5f));
                float s0 = math.max(0f, center - half), s1 = math.min(total, center + half);
                a = PointAt(pts, len, s0);
                b = PointAt(pts, len, s1);
                if (!checkStraight || IsStraight(pts, len, s0, s1, b - a))
                {
                    s = center;
                    ok = true;
                    break;
                }
            }
            if (!ok) return;

            float2 at = PointAt(pts, len, s);
            float2 dir = checkStraight ? b - a : DirectionAt(pts, len, s);
            if (math.lengthsq(dir) < 1e-10f) return;
            dir = math.normalize(dir);
            var req = Requests[r];
            if ((req.Flags & OmtLabelFlags.Reverse) != 0) dir = -dir;

            float2 uv = at / TilePixels;
            if (uv.x < 0f || uv.y < 0f || uv.x >= 1f || uv.y >= 1f) return;

            bool rotates = req.IconMode != OmtIconMode.Shield;
            candidates.Add(new Candidate
            {
                Request = r,
                TilePx = at,
                Anchor = GroundAnchors.SampleGround(uv, Grid, LocalFrame) + new float3(0f, Lift, 0f),
                // Tile UV x/y map to Mercator x/y, which map to scene X/Z.
                Tangent = rotates ? new float3(dir.x, 0f, dir.y) : float3.zero,
                Angle = rotates ? math.atan2(dir.y, dir.x) : 0f,
                MinTier = tier,
                Order = candidates.Length,
            });
        }

        private static float2 PointAt(NativeList<float2> pts, NativeList<float> len, float s)
        {
            for (int i = 1; i < pts.Length; i++)
            {
                if (len[i] >= s)
                {
                    float seg = len[i] - len[i - 1];
                    float t = seg > 0f ? (s - len[i - 1]) / seg : 0f;
                    return math.lerp(pts[i - 1], pts[i], math.saturate(t));
                }
            }
            return pts[pts.Length - 1];
        }

        private static float2 DirectionAt(NativeList<float2> pts, NativeList<float> len, float s)
        {
            for (int i = 1; i < pts.Length; i++)
                if (len[i] >= s) return pts[i] - pts[i - 1];
            return pts[pts.Length - 1] - pts[pts.Length - 2];
        }

        private static bool IsStraight(NativeList<float2> pts, NativeList<float> len, float s0, float s1, float2 chord)
        {
            if (math.lengthsq(chord) < 1e-10f) return false;
            float2 c = math.normalize(chord);
            for (int i = 1; i < pts.Length; i++)
            {
                if (len[i] <= s0 || len[i - 1] >= s1) continue;
                float2 d = pts[i] - pts[i - 1];
                if (math.lengthsq(d) < 1e-10f) continue;
                float cos = math.dot(math.normalize(d), c);
                if (cos < math.cos(MaxBendRadians)) return false;
            }
            return true;
        }

        // ---- Collision -----------------------------------------------------------

        private static float4 RotatedBox(float4 box, float angle)
        {
            if (angle == 0f) return box;
            float cs = math.cos(angle), sn = math.sin(angle);
            float4 result = new float4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                float2 p = new float2((i & 1) == 0 ? box.x : box.z, (i & 2) == 0 ? box.y : box.w);
                float2 q = new float2(p.x * cs - p.y * sn, p.x * sn + p.y * cs);
                result = new float4(math.min(result.xy, q), math.max(result.zw, q));
            }
            return result;
        }

        private static int CellKey(int tier, int cx, int cy) =>
            (int)math.hash(new int3(tier, cx, cy));

        private static bool Fits(ref NativeParallelMultiHashMap<int, float4> grid, int tier, float2 tilePx, float4 box)
        {
            float2 c = tilePx * math.exp2(tier);
            float4 b = new float4(c + box.xy, c + box.zw);
            int2 min = (int2)math.floor(b.xy / CellSize), max = (int2)math.floor(b.zw / CellSize);
            for (int cy = min.y; cy <= max.y; cy++)
            for (int cx = min.x; cx <= max.x; cx++)
            {
                if (!grid.TryGetFirstValue(CellKey(tier, cx, cy), out float4 o, out var it)) continue;
                do
                {
                    if (b.x < o.z && b.z > o.x && b.y < o.w && b.w > o.y) return false;
                } while (grid.TryGetNextValue(out o, ref it));
            }
            return true;
        }

        private static void Insert(ref NativeParallelMultiHashMap<int, float4> grid, int tier, float2 tilePx, float4 box)
        {
            float2 c = tilePx * math.exp2(tier);
            float4 b = new float4(c + box.xy, c + box.zw);
            int2 min = (int2)math.floor(b.xy / CellSize), max = (int2)math.floor(b.zw / CellSize);
            for (int cy = min.y; cy <= max.y; cy++)
            for (int cx = min.x; cx <= max.x; cx++)
                grid.Add(CellKey(tier, cx, cy), b);
        }

        // ---- Output --------------------------------------------------------------

        private void Emit(Candidate cand, OmtLabelRequest req, OmtLabelRuleData rule, Layout layout,
            NativeList<Quad> quads, float minZoom, float maxZoom, ref NativeList<int> quadSlots)
        {
            float rotation = math.lengthsq(cand.Tangent) > 0f
                ? ((req.Flags & OmtLabelFlags.KeepDirection) != 0 ? 2f : 1f)
                : 0f;
            Color32 textColor = req.ColorOverride.a > 0 ? req.ColorOverride : rule.Color;
            var white = new Color32(255, 255, 255, 255);
            var uv2 = new float4(cand.Tangent, rotation);

            Bounds[0] = math.min(Bounds[0], cand.Anchor);
            Bounds[1] = math.max(Bounds[1], cand.Anchor);

            for (int q = 0; q < layout.QuadCount; q++)
            {
                var quad = quads[layout.QuadStart + q];
                float mode = quad.Icon ? 1f : 0f;
                Color32 color = quad.Icon ? white : textColor;
                for (int v = 0; v < 4; v++)
                {
                    bool right = v == 1 || v == 2;
                    bool top = v >= 2;
                    Positions.Add(cand.Anchor);
                    Vertices.Add(new OmtLabelVertex
                    {
                        Uv0 = new float4(right ? quad.Uv.z : quad.Uv.x, top ? quad.Uv.w : quad.Uv.y, mode, rule.HaloWidth),
                        Uv1 = new float4(right ? quad.Rect.z : quad.Rect.x, top ? quad.Rect.w : quad.Rect.y, minZoom, maxZoom),
                        Uv2 = uv2,
                    });
                    Colors.Add(new OmtLabelColors { Color = color, Halo = rule.HaloColor });
                }
                quadSlots.Add(quad.Slot);
            }
        }

        private void BuildIndices(NativeList<int> quadSlots)
        {
            int maxSlot = -1;
            for (int i = 0; i < quadSlots.Length; i++) maxSlot = math.max(maxSlot, quadSlots[i]);
            for (int slot = 0; slot <= maxSlot; slot++)
            {
                int start = Indices.Length;
                for (int q = 0; q < quadSlots.Length; q++)
                {
                    if (quadSlots[q] != slot) continue;
                    int v = q * 4;
                    Indices.Add(v);
                    Indices.Add(v + 2);
                    Indices.Add(v + 1);
                    Indices.Add(v);
                    Indices.Add(v + 3);
                    Indices.Add(v + 2);
                }
                if (Indices.Length > start) SubMeshes.Add(new int3(slot, start, Indices.Length - start));
            }
        }
    }
}
