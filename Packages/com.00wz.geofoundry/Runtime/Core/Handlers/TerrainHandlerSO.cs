using UnityEngine;

namespace GeoFoundry.Core.Handlers
{
    /// <summary>
    /// Base class for terrain handlers configured in the Unity Editor as ScriptableObject assets.
    /// Assign the asset to <c>GeoFoundryBootstrapper.TerrainHandler</c>.
    /// </summary>
    public abstract class TerrainHandlerSO : ScriptableObject, ITerrainHandler
    {
        /// <inheritdoc/>
        public abstract Awaitable<ITerrainHandlerInstance> OnTerrainLoadedAsync(TerrainLoadContext ctx);
    }
}
