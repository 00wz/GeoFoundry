using System.Threading;
using GeoFoundry.Core;
using GeoFoundry.Core.Mvt;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Result of <see cref="AsyncBuildingPropertyReader.ReadAsync"/>: per-feature (non-compacted) arrays.
    /// The caller (usually <see cref="OmtBuildingHandler"/>) owns them, passes them as borrowed input to
    /// <see cref="GeoFoundry.Geometry.AsyncGeometryWorker.BuildBuildingTerrainMeshAsync"/> and releases them with <see cref="Dispose"/>.
    /// </summary>
    internal readonly struct BuildingProperties
    {
        public readonly NativeArray<bool> Hidden;
        public readonly NativeArray<float> TopMeters;
        public readonly NativeArray<float> BottomMeters;
        public readonly NativeArray<Color32> Colors;

        public BuildingProperties(
            NativeArray<bool> hidden, NativeArray<float> topMeters, NativeArray<float> bottomMeters, NativeArray<Color32> colors)
        {
            Hidden = hidden;
            TopMeters = topMeters;
            BottomMeters = bottomMeters;
            Colors = colors;
        }

        public void Dispose()
        {
            Hidden.Dispose();
            TopMeters.Dispose();
            BottomMeters.Dispose();
            Colors.Dispose();
        }
    }

    /// <summary>Interpretation options of <see cref="BuildingPropertiesJob"/>.</summary>
    internal struct BuildingPropertyOptions
    {
        public float DefaultHeight;
        public float HeightScale;
        public float MinHeight;
        public Color32 DefaultColor;
        public bool LinearizeColours;
        public bool UseColour;

        /// <summary>6 m default height, no scaling, no minimum, opaque white, colours as is.</summary>
        public static BuildingPropertyOptions Default => new BuildingPropertyOptions
        {
            DefaultHeight = 6f,
            HeightScale = 1f,
            MinHeight = 0f,
            DefaultColor = new Color32(255, 255, 255, 255),
            LinearizeColours = false,
            UseColour = true,
        };
    }

    /// <summary>
    /// Standalone async helper that interprets the OMT building schema with <see cref="BuildingPropertiesJob"/>.
    /// It knows nothing about geometry: <see cref="OmtBuildingHandler"/> awaits it first and then passes the result
    /// to <see cref="GeoFoundry.Geometry.AsyncGeometryWorker.BuildBuildingTerrainMeshAsync"/>, which only builds geometry and does not
    /// depend on the schema.
    /// </summary>
    internal static class AsyncBuildingPropertyReader
    {
        /// <summary>
        /// Reads building properties for every feature of <paramref name="layer"/> on a background job.
        /// Returns <c>null</c> if <paramref name="ct"/> is cancelled before the result is ready; the caller must
        /// then build nothing further. Control returns only after the job has completed (it reads the borrowed
        /// <paramref name="layer"/>), including on cancellation.
        /// </summary>
        public static Awaitable<BuildingProperties?> ReadAsync(NativeMvtLayer layer, CancellationToken ct) =>
            ReadAsync(layer, BuildingPropertyOptions.Default, ct);

        /// <summary>Same as <see cref="ReadAsync(NativeMvtLayer, CancellationToken)"/> with explicit options.</summary>
        public static async Awaitable<BuildingProperties?> ReadAsync(
            NativeMvtLayer layer, BuildingPropertyOptions options, CancellationToken ct)
        {
            int featureCount = math.max(1, layer.FeatureCount);
            var hidden = new NativeArray<bool>(featureCount, Allocator.Persistent);
            var topMeters = new NativeArray<float>(featureCount, Allocator.Persistent);
            var bottomMeters = new NativeArray<float>(featureCount, Allocator.Persistent);
            var colors = new NativeArray<Color32>(featureCount, Allocator.Persistent);

            var job = new BuildingPropertiesJob
            {
                FeatureTagRange = layer.FeatureTagRangeSlice,
                Tags = layer.Tags,
                Keys = layer.Keys,
                Values = layer.Values,
                Hidden = hidden,
                TopMeters = topMeters,
                BottomMeters = bottomMeters,
                Colors = colors,
                DefaultTopMeters = options.DefaultHeight,
                DefaultBottomMeters = 0f,
                HeightScale = options.HeightScale,
                MinHeightMeters = options.MinHeight,
                DefaultColor = options.DefaultColor,
                LinearizeColours = options.LinearizeColours,
                UseColour = options.UseColour,
            };

            JobHandle handle = job.Schedule();
            bool completed = await handle.WaitNonBlockingAsync(ct);

            if (!completed)
            {
                hidden.Dispose();
                topMeters.Dispose();
                bottomMeters.Dispose();
                colors.Dispose();
                return null;
            }

            return new BuildingProperties(hidden, topMeters, bottomMeters, colors);
        }
    }
}
