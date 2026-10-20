using GeoFoundry.Core.Mvt;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Render.Layers.OpenMapTiles
{
    /// <summary>
    /// Interprets the OMT building layer schema (<c>hide_3d</c>, <c>render_height</c>, <c>render_min_height</c>,
    /// <c>colour</c>) on the Job System. Fully standalone: it knows nothing about geometry, polygons or terrain.
    /// Reads properties directly from the raw slices (<see cref="NativeMvtLayer.Tags"/>,
    /// <see cref="NativeMvtLayer.Keys"/>, <see cref="NativeMvtLayer.Values"/>): the internal fields of
    /// <see cref="NativeMvtTile"/> are not accessible outside <c>GeoFoundry.Core</c>.
    ///
    /// Outputs are per-feature arrays (non-compacted, length = number of features in the layer). They are passed
    /// as plain input to <see cref="GeoFoundry.Geometry.AsyncGeometryWorker.BuildBuildingTerrainMeshAsync"/> (terrain-conforming), which
    /// uses <see cref="TopMeters"/> and <see cref="BottomMeters"/> as offsets above the terrain at every point rather
    /// than as a single height. This job only interprets the schema and does not choose the geometry strategy.
    /// </summary>
    [BurstCompile]
    internal struct BuildingPropertiesJob : IJob
    {
        private const float DefaultTopMeters = 6f;
        private const float DefaultBottomMeters = 0f;

        [ReadOnly] public NativeArray<int2> FeatureTagRange;
        [ReadOnly] public NativeArray<int2> Tags;
        [ReadOnly] public NativeArray<FixedString64Bytes> Keys;
        [ReadOnly] public NativeArray<MvtValue> Values;

        public NativeArray<bool> Hidden;
        public NativeArray<float> TopMeters;
        public NativeArray<float> BottomMeters;
        /// <summary>
        /// Per-feature color that the geometry job assigns to every vertex of the feature
        /// (<c>albedo *= vertexColor</c> in the shader). Opaque white (255,255,255,255) means no color override
        /// (<c>colour</c> is optional) and is the identity for the multiplication.
        /// </summary>
        public NativeArray<Color32> Colors;

        public void Execute()
        {
            FixedString64Bytes keyHide3d = "hide_3d";
            FixedString64Bytes keyRenderHeight = "render_height";
            FixedString64Bytes keyRenderMinHeight = "render_min_height";
            FixedString64Bytes keyColour = "colour";

            for (int fi = 0; fi < FeatureTagRange.Length; fi++)
            {
                int2 tagRange = FeatureTagRange[fi];

                // AsLong()/AsDouble() do not touch managed strings, unlike AsBool() for Kind.String, which calls
                // FixedString.ToString() and is not allowed in Burst.
                bool hidden = TryGetValue(tagRange, keyHide3d, out var hv) && hv.AsLong() != 0;
                float top = TryGetValue(tagRange, keyRenderHeight, out var tv) ? (float)tv.AsDouble() : DefaultTopMeters;
                float bottom = TryGetValue(tagRange, keyRenderMinHeight, out var bv) ? (float)bv.AsDouble() : DefaultBottomMeters;

                Color32 color = new Color32(255, 255, 255, 255);
                if (TryGetValue(tagRange, keyColour, out var cv) &&
                    cv.TryGetRawString(out var raw) && TryParseHexColor(raw, out var rgba))
                {
                    color = rgba;
                }

                Hidden[fi] = hidden;
                TopMeters[fi] = top;
                BottomMeters[fi] = bottom;
                Colors[fi] = color;
            }
        }

        /// <summary>Linear scan over the feature's tag range; equivalent to the managed
        /// <see cref="NativeMvtPropertyBag.TryGetValue"/>, but on the raw slices.</summary>
        private bool TryGetValue(int2 tagRange, FixedString64Bytes key, out MvtValue value)
        {
            for (int i = 0; i < tagRange.y; i++)
            {
                int2 tag = Tags[tagRange.x + i];
                if (Keys[tag.x].Equals(key))
                {
                    value = Values[tag.y];
                    return true;
                }
            }
            value = default;
            return false;
        }

        // ---- Hex color parser (#rgb, #rrggbb, #rrggbbaa; leading '#' optional) on FixedString128Bytes ----

        private static bool TryParseHexColor(FixedString128Bytes raw, out Color32 rgba)
        {
            rgba = default;
            int length = raw.Length;
            if (length == 0) return false;
            int start = raw[0] == '#' ? 1 : 0;
            int len = length - start;

            if (len == 3)
            {
                if (!TryHexDigit(raw[start], out int r) || !TryHexDigit(raw[start + 1], out int g) || !TryHexDigit(raw[start + 2], out int b)) return false;
                rgba = new Color32((byte)(r * 17), (byte)(g * 17), (byte)(b * 17), 255);
                return true;
            }
            if (len == 6)
            {
                if (!TryHexByte(raw, start, out byte r) || !TryHexByte(raw, start + 2, out byte g) || !TryHexByte(raw, start + 4, out byte b)) return false;
                rgba = new Color32(r, g, b, 255);
                return true;
            }
            if (len == 8)
            {
                if (!TryHexByte(raw, start, out byte r) || !TryHexByte(raw, start + 2, out byte g) || !TryHexByte(raw, start + 4, out byte b) || !TryHexByte(raw, start + 6, out byte a)) return false;
                rgba = new Color32(r, g, b, a);
                return true;
            }
            return false;
        }

        private static bool TryHexDigit(byte c, out int v)
        {
            if (c >= '0' && c <= '9') { v = c - '0'; return true; }
            if (c >= 'a' && c <= 'f') { v = 10 + c - 'a'; return true; }
            if (c >= 'A' && c <= 'F') { v = 10 + c - 'A'; return true; }
            v = 0; return false;
        }

        private static bool TryHexByte(FixedString128Bytes s, int offset, out byte v)
        {
            if (!TryHexDigit(s[offset], out int hi) || !TryHexDigit(s[offset + 1], out int lo)) { v = 0; return false; }
            v = (byte)((hi << 4) | lo);
            return true;
        }
    }
}
