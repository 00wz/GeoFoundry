using GeoFoundry.Core.Coordinates;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Ground
{
    /// <summary>
    /// Fills ground grid heights and normals from DEM data for <see cref="DemGroundGridProvider"/>. The terrain
    /// <c>Mesh</c> itself is built from the finished grid by <c>TerrainBuildJob</c> /
    /// <see cref="GeoFoundry.Geometry.AsyncGeometryWorker.BuildTerrainMeshAsync"/>; this class covers only the
    /// heightmap stage (main thread, outside async geometry).
    /// </summary>
    internal static class TerrainMeshBuilder
    {
        /// <summary>
        /// Fills the height and normal arrays from a <see cref="PaddedHeightSampler"/> over the 9 DEM tiles around
        /// the central one: bilinear heights, and cross-product normals computed over a padded border so normals
        /// match between neighboring terrain tiles.
        /// </summary>
        public static void FillGridFromPaddedSampler(
            PaddedHeightSampler sampler,
            int gridResolution,
            TileCoord tile,
            NativeArray<float> heightsOut,
            NativeArray<Vector3> normalsOut)
        {
            if (gridResolution < 1) throw new System.ArgumentOutOfRangeException(nameof(gridResolution));
            int vps = gridResolution + 1;
            int pvps = gridResolution + 3;
            int vertCount = vps * vps;
            bool writeHeights = heightsOut.IsCreated && heightsOut.Length == vertCount;
            bool writeNormals = normalsOut.IsCreated && normalsOut.Length == vertCount;

            var padded = new float[pvps * pvps];
            for (int pj = 0; pj < pvps; pj++)
            for (int pi = 0; pi < pvps; pi++)
            {
                double u = (double)(pi - 1) / gridResolution;
                double v = (double)(pj - 1) / gridResolution;
                padded[pj * pvps + pi] = sampler.SampleBilinear(u, v);
            }

            var bounds = tile.BoundsMeters();
            double cellSizeX = (bounds.z - bounds.x) / gridResolution;
            double cellSizeY = (bounds.w - bounds.y) / gridResolution;

            for (int j = 0; j <= gridResolution; j++)
            for (int i = 0; i <= gridResolution; i++)
            {
                int idx = j * vps + i;
                int pi = i + 1, pj = j + 1;
                float h = padded[pj * pvps + pi];
                if (writeHeights) heightsOut[idx] = h;

                if (writeNormals)
                {
                    float hL = padded[pj * pvps + (pi - 1)];
                    float hR = padded[pj * pvps + (pi + 1)];
                    float hD = padded[(pj - 1) * pvps + pi];
                    float hU = padded[(pj + 1) * pvps + pi];
                    var dx = new Vector3((float)(2 * cellSizeX), hR - hL, 0);
                    var dz = new Vector3(0, hU - hD, (float)(2 * cellSizeY));
                    var n = Vector3.Cross(dz, dx).normalized;
                    if (n.y < 0) n = -n;
                    normalsOut[idx] = n;
                }
            }
        }
    }
}
