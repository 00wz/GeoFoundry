using UnityEngine;

namespace GeoFoundry.Core.Handlers
{
    /// <summary>
    /// Base class for MVT layer handlers configured in the Unity Editor as ScriptableObject assets.
    /// Create an asset from the <c>GeoFoundry/Layers/...</c> menu and add it to
    /// <c>GeoFoundryBootstrapper.LayerHandlers</c>; it is registered when the map starts.
    /// </summary>
    public abstract class LayerHandlerSO : ScriptableObject, ILayerHandler
    {
        /// <inheritdoc/>
        public abstract string LayerName { get; }

        /// <inheritdoc/>
        public abstract Awaitable<ILayerHandlerInstance> OnLayerLoadedAsync(LayerLoadContext ctx);
    }
}
