using System;
using GeoFoundry.Core.Ground;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Prepares geometry job inputs. The Job System requires ALL NativeContainer fields of a job (including those
    /// nested in <see cref="GroundGrid"/>) to be validly constructed at <c>Schedule()</c> time, even if
    /// <c>Execute()</c> does not use them, so an optional input is replaced with a valid zero-length array owned by
    /// the call's <see cref="GeometryBuffers"/>.
    /// </summary>
    internal static class JobInputs
    {
        /// <summary>
        /// Throws <see cref="ArgumentException"/> if the tile's ground grid is not set. The grid is required (see
        /// <see cref="AsyncGeometryWorker"/>) and is checked before the call's buffers are allocated.
        /// </summary>
        public static void RequireGrid(GroundGrid grid, string paramName)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Tile grid is not set: without terrain elevation a flat grid is required (FlatGroundGridProvider).", paramName);
        }

        /// <summary>
        /// Throws <see cref="ArgumentException"/> unless all 9 slots of <paramref name="grids"/> are set; used by
        /// jobs that read neighbors. The message names the first empty slot.
        /// </summary>
        public static void RequireAllGrids(TileGroundGrids grids, string paramName)
        {
            RequireSlot(grids.Center, nameof(TileGroundGrids.Center), paramName);
            RequireSlot(grids.North, nameof(TileGroundGrids.North), paramName);
            RequireSlot(grids.South, nameof(TileGroundGrids.South), paramName);
            RequireSlot(grids.East, nameof(TileGroundGrids.East), paramName);
            RequireSlot(grids.West, nameof(TileGroundGrids.West), paramName);
            RequireSlot(grids.NorthEast, nameof(TileGroundGrids.NorthEast), paramName);
            RequireSlot(grids.NorthWest, nameof(TileGroundGrids.NorthWest), paramName);
            RequireSlot(grids.SouthEast, nameof(TileGroundGrids.SouthEast), paramName);
            RequireSlot(grids.SouthWest, nameof(TileGroundGrids.SouthWest), paramName);
        }

        private static void RequireSlot(GroundGrid grid, string slot, string paramName)
        {
            if (!grid.IsValid)
                throw new ArgumentException(
                    $"Grid {slot} is not set: TileBuilder provides neighbor grids only when GeoFoundryConfig.LoadNeighborGrids is enabled.",
                    paramName);
        }

        /// <summary>
        /// Handlers without per-feature color (surfaces and roads under OMT) pass <c>default</c>; it is replaced
        /// here with a valid zero-length array, and the job then writes opaque white.
        /// </summary>
        public static NativeArray<Color32> PrepareFeatureColors(NativeArray<Color32> featureColors, GeometryBuffers owner) =>
            featureColors.IsCreated ? featureColors : owner.Own(new NativeArray<Color32>(0, Allocator.Persistent));

        /// <summary>
        /// Creates a <see cref="BuildingBuildJob.SamplerGridView"/> for one named slot of
        /// <see cref="TileGroundGrids"/> (<c>Center</c>, <c>North</c>, ...). The caller validates the slots: by the
        /// <see cref="TileGroundGrids"/> contract they are all valid when neighbors are present.
        /// </summary>
        public static BuildingBuildJob.SamplerGridView MakeSamplerGridView(GroundGrid grid) =>
            new BuildingBuildJob.SamplerGridView
            {
                Heights = grid.Heights,
                Bounds = grid.BoundsMeters,
                VertsPerSide = grid.VertsPerSide,
            };
    }
}
