using System.Threading;
using GeoFoundry.Core;
using GeoFoundry.Core.Mvt;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Render.Layers.OpenMapTiles
{
    /// <summary>
    /// Result of <see cref="AsyncBuildingPropertyReader.ReadAsync"/>: per-feature (non-compacted) arrays.
    /// The caller (usually <c>BuildingsLayerHandler</c>) owns them, passes them as borrowed input to
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

    /// <summary>
    /// Standalone async helper that interprets the OMT building schema with <see cref="BuildingPropertiesJob"/>.
    /// It knows nothing about geometry: <see cref="BuildingsLayerHandler"/> awaits it first and then passes the result
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
        public static async Awaitable<BuildingProperties?> ReadAsync(NativeMvtLayer layer, CancellationToken ct)
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
