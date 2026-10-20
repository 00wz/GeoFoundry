using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// The layer's meshes plus the source feature index (into <c>NativeMvtLayer.Features</c>) of each output mesh.
    /// Neither the feature id nor any rendering property (<c>colour</c> etc.) affects geometry, so they are not
    /// passed through the job; the handler resolves them itself via <c>FeatureIndices[i]</c> →
    /// <c>NativeMvtFeature.Id</c> or the corresponding property array. Shared by all per-feature build jobs
    /// (surfaces, roads, buildings).
    /// </summary>
    internal readonly struct GeometryResult
    {
        public readonly Mesh[] Meshes;
        public readonly int[] FeatureIndices;

        public GeometryResult(Mesh[] meshes, int[] featureIndices)
        {
            Meshes = meshes;
            FeatureIndices = featureIndices;
        }
    }
}
