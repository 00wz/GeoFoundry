using System;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Base class of the built-in OpenMapTiles handlers: zoom range filtering, mesh collection
    /// (<see cref="OmtTileOutput"/>) and the cancellation contract of <see cref="ILayerHandler.OnLayerLoadedAsync"/>.
    /// Derived handlers implement <see cref="BuildAsync"/>, which schedules jobs over the borrowed layer data, awaits
    /// them and adds the resulting meshes to the output.
    /// </summary>
    public abstract class OmtLayerHandler : LayerHandlerSO
    {
        [Tooltip("Tiles with a lower zoom are skipped by this handler.")]
        [SerializeField, Range(0, 22)] private int _minZoom;

        [Tooltip("Tiles with a higher zoom are skipped by this handler.")]
        [SerializeField, Range(0, 22)] private int _maxZoom = 22;

        /// <summary>Gets or sets the lowest tile zoom processed by the handler.</summary>
        public int MinZoom { get => _minZoom; set => _minZoom = value; }

        /// <summary>Gets or sets the highest tile zoom processed by the handler.</summary>
        public int MaxZoom { get => _maxZoom; set => _maxZoom = value; }

        public sealed override async Awaitable<ILayerHandlerInstance> OnLayerLoadedAsync(LayerLoadContext ctx)
        {
            int z = ctx.TileCoord.Z;
            if (z < _minZoom || z > _maxZoom || ctx.Layer.FeatureCount == 0) return null;

            var output = new OmtTileOutput(ctx, LayerName);
            try
            {
                await BuildAsync(ctx, output);
            }
            catch (Exception)
            {
                output.DestroyAll();
                throw;
            }

            if (ctx.Ct.IsCancellationRequested)
            {
                output.DestroyAll();
                return null;
            }
            return output.CreateInstance();
        }

        /// <summary>
        /// Builds the layer content of one tile into <paramref name="output"/>. Every job scheduled over
        /// <see cref="LayerLoadContext.Layer"/>/<see cref="LayerLoadContext.Grids"/> must be awaited before returning,
        /// including on cancellation (use <see cref="GeoFoundry.Core.JobHandleAwaitExtensions.WaitNonBlockingAsync"/>
        /// or <c>AsyncGeometryWorker</c>). On cancellation the base class destroys whatever was added.
        /// </summary>
        protected abstract Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output);

        /// <summary>Gets the tile's style pixel size in Mercator meters (see <see cref="OmtZoom"/>).</summary>
        protected static float MetersPerPixel(LayerLoadContext ctx) => OmtZoom.MetersPerPixel(ctx.TileCoord.Z);

#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            if (_maxZoom < _minZoom) _maxZoom = _minZoom;
            ValidateMaterials();
        }

        /// <summary>
        /// Override to check the draw order of the assigned materials with <see cref="CheckPriority"/>.
        /// </summary>
        protected virtual void ValidateMaterials() { }

        /// <summary>
        /// Logs a warning if <paramref name="material"/> does not have the Sorting Priority recommended by
        /// <see cref="OmtDrawOrder"/>.
        /// </summary>
        protected void CheckPriority(Material material, int expected, string slot)
        {
            if (material == null) return;
            int actual = material.HasProperty("_QueueOffset")
                ? Mathf.RoundToInt(material.GetFloat("_QueueOffset"))
                : material.renderQueue - 2000;
            if (actual != expected)
            {
                Debug.LogWarning(
                    $"{name}: material '{material.name}' ({slot}) has Sorting Priority {actual}, the recommended value " +
                    $"is {expected} (see OmtDrawOrder). Layers may overlap in the wrong order.", this);
            }
        }
#endif
    }
}
