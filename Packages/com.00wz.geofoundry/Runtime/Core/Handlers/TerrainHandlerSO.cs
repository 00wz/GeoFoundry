using UnityEngine;

namespace GeoFoundry.Core.Handlers
{
    /// <summary>
    /// Base class for terrain handlers configured in the Unity Editor as ScriptableObject assets.
    /// Assign the asset to <c>GeoFoundryConfig.TerrainHandler</c>. The terrain is required by the draped layer
    /// shaders: it is their occlusion base and renders first.
    /// </summary>
    public abstract class TerrainHandlerSO : ScriptableObject, ITerrainHandler
    {
        /// <inheritdoc/>
        public abstract Awaitable<ITerrainHandlerInstance> OnTerrainLoadedAsync(TerrainLoadContext ctx);
    }
}
