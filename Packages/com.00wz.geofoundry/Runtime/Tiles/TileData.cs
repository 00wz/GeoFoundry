using System;
using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Ground;
using UnityEngine;

namespace GeoFoundry.Tiles
{
    /// <summary>
    /// Owns all resources of one tile: ground grid handles (the center and, optionally, 8 neighbors), the terrain
    /// instance and layer instances.
    ///
    /// Live from the constructor: it immediately positions its root relative to the current floating-origin pivot
    /// and subscribes to <see cref="IFloatingOriginController.PivotChanged"/> before <see cref="TileBuilder"/> starts
    /// filling it (see <see cref="TileBuilder.PopulateAsync"/>). This removes the race between a rebase and the
    /// asynchronous build: whenever a rebase happens, the root and any attached instances handle it the same way as
    /// fully loaded tiles. Content is attached incrementally via <see cref="AttachTerrain"/> and
    /// <see cref="AttachLayerInstance"/>; each instance is synchronized with the current pivot and the tile's
    /// active state (see <see cref="SetActive"/>) at the moment it is attached.
    ///
    /// <see cref="Dispose"/> releases everything in order: subscription, instances, GameObject, grid handles
    /// (which free the native arrays).
    /// </summary>
    internal sealed class TileData : IDisposable
    {
        public readonly TileCoord Coord;

        /// <summary>Transform of the tile root; the parent for GameObjects created by handlers.</summary>
        public Transform Root => _tileRoot.transform;

        public int LayerInstanceCount => _layerInstances.Count;
        public bool HasTerrain => _terrainInstance != null;

        /// <summary>Whether the tile is currently enabled (visible in the scene). See <see cref="SetActive"/>.</summary>
        public bool IsActive { get; private set; } = true;

        /// <summary>
        /// Memory held by the tile content: the sum of <c>MemoryUsage</c> of attached terrain and layer instances.
        /// Accumulated in <see cref="AttachTerrain"/> and <see cref="AttachLayerInstance"/>, so it grows while the
        /// tile is building. Shared ground grid handles are excluded, since they are shared with neighboring tiles
        /// (see <c>DemGroundGridProvider.MemoryBytes</c>).
        /// </summary>
        public TileMemoryUsage MemoryUsage { get; private set; }

        private readonly GameObject _tileRoot;
        private readonly IFloatingOriginController _originController;
        private ITerrainHandlerInstance _terrainInstance;
        private readonly List<ILayerHandlerInstance> _layerInstances = new();
        private GroundGridHandle[] _handles = Array.Empty<GroundGridHandle>();
        private bool _disposed;

        public TileData(TileCoord coord, GameObject tileRoot, IFloatingOriginController originController)
        {
            Coord = coord;
            _tileRoot = tileRoot ?? throw new ArgumentNullException(nameof(tileRoot));
            _originController = originController ?? throw new ArgumentNullException(nameof(originController));

            // Correct position before any content plus the subscription: from here on the tile is rebase-safe,
            // even if the build has not started yet.
            ApplyPivot(_originController.Current);
            _originController.PivotChanged += OnPivotChanged;
        }

        private void OnPivotChanged(PivotChangedEventArgs args) => ApplyPivot(args.NewPivot);

        /// <summary>
        /// Positions the tile root Transform relative to the global floating-origin pivot (not the local frame the
        /// geometry is baked in; see <see cref="GeoFoundry.Core.Handlers.LayerLoadContext.LocalFrame"/>) and notifies
        /// all attached layer and terrain instances (<see cref="ILayerHandlerInstance.OnOriginShifted"/>,
        /// <see cref="ITerrainHandlerInstance.OnOriginShifted"/>) in case a handler does not rely on the Transform
        /// hierarchy. The position is always recomputed from <see cref="Coord"/>, not by accumulating deltas.
        /// </summary>
        public void ApplyPivot(MercatorPivot pivot)
        {
            var world = pivot.WorldFromMercator(Coord.CenterMeters());
            _tileRoot.transform.localPosition = new Vector3(world.x, world.y, world.z);

            foreach (var inst in _layerInstances) inst.OnOriginShifted(pivot);
            _terrainInstance?.OnOriginShifted(pivot);
        }

        /// <summary>
        /// Attaches the terrain instance and immediately synchronizes it with the current pivot, in case a rebase
        /// happened after the build started.
        /// </summary>
        public void AttachTerrain(ITerrainHandlerInstance instance)
        {
            if (_terrainInstance != null) MemoryUsage -= _terrainInstance.MemoryUsage;
            _terrainInstance = instance;
            MemoryUsage += instance.MemoryUsage;
            instance.OnOriginShifted(_originController.Current);
            instance.OnActiveChanged(IsActive);
        }

        /// <summary>Same as <see cref="AttachTerrain"/>, for layer instances.</summary>
        public void AttachLayerInstance(ILayerHandlerInstance instance)
        {
            _layerInstances.Add(instance);
            MemoryUsage += instance.MemoryUsage;
            instance.OnOriginShifted(_originController.Current);
            instance.OnActiveChanged(IsActive);
        }

        public void AttachHandles(GroundGridHandle[] handles) => _handles = handles ?? Array.Empty<GroundGridHandle>();

        /// <summary>
        /// Enables or disables the tile: toggles the root GameObject and notifies all attached instances
        /// (<see cref="ILayerHandlerInstance.OnActiveChanged"/>, <see cref="ITerrainHandlerInstance.OnActiveChanged"/>).
        /// Unlike <see cref="Dispose"/>, releases nothing: the tile stays loaded and can be enabled again without
        /// reloading (e.g. when it is temporarily replaced by tiles of another zoom level).
        /// </summary>
        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;

            _tileRoot.SetActive(active);
            _terrainInstance?.OnActiveChanged(active);
            foreach (var inst in _layerInstances) inst.OnActiveChanged(active);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Without unsubscribing, the long-lived controller's event would keep the tile alive and call ApplyPivot
            // on a destroyed GameObject on the next rebase.
            _originController.PivotChanged -= OnPivotChanged;

            // Each step is isolated: a failure is logged and does not prevent the following releases, which would
            // otherwise leak the tile GameObject and the ground grid native arrays.

            // Layer instances destroy their meshes and GameObjects.
            foreach (var inst in _layerInstances)
                try { inst.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }

            // Terrain GameObject and mesh.
            try { _terrainInstance?.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }

            // Tile root GameObject (parent of layer objects).
            try
            {
                if (_tileRoot != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(_tileRoot);
                    else UnityEngine.Object.DestroyImmediate(_tileRoot);
                }
            }
            catch (Exception ex) { Debug.LogException(ex); }

            // Ground grid handles free their native arrays when the ref count reaches 0.
            foreach (var h in _handles)
                try { h.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
