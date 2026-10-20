using System.Collections.Generic;
using GeoFoundry.Core.Coordinates;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tiles.Diagnostics
{
    /// <summary>
    /// Editor debug overlay for tiles: tile frames (gizmos) and a statistics panel in the Scene view.
    /// Built from <see cref="TileLifecycleManager.GetDebugSnapshot"/>; call it from <c>OnDrawGizmos</c>.
    /// <c>OnDrawGizmos</c> is not called in player builds, so the overlay costs nothing there.
    ///
    /// The frame color shows the tile's main state:
    /// <list type="bullet">
    /// <item>green — rendered;</item>
    /// <item>yellow — building or waiting for a load slot;</item>
    /// <item>red — empty: admitted but without content, usually a failed load
    ///   (see <see cref="TileDebugInfo.IsEmpty"/>);</item>
    /// <item>cyan — admitted and in memory, but not rendered (e.g. out of view);</item>
    /// <item>magenta — denied by the budget;</item>
    /// <item>gray — not wanted by the selector: warm cache or a cancelling build.</item>
    /// </list>
    /// </summary>
    public sealed class TileDebugOverlay
    {
        private const double Megabyte = 1024.0 * 1024.0;

        private readonly List<TileDebugInfo> _tiles = new(256);
        private GUIStyle _panelStyle;

        /// <summary>Draws the tile frames and, in the Editor, the statistics panel.</summary>
        /// <param name="manager">The manager whose tiles are drawn.</param>
        /// <param name="pivot">The current floating-origin pivot, used for world positions of the frames.</param>
        /// <param name="groundGridBytes">
        /// Memory of the shared ground grids (<c>GroundGridProvider.MemoryBytes</c>). It is outside the tile budget
        /// but lives in the same heap on WebGL; 0 hides the row.
        /// </param>
        public void Draw(TileLifecycleManager manager, MercatorPivot pivot, long groundGridBytes)
        {
            manager.GetDebugSnapshot(_tiles);
            foreach (var tile in _tiles) DrawTile(tile, pivot);

#if UNITY_EDITOR
            DrawPanel(manager, TileDebugSummary.From(_tiles), groundGridBytes);
#endif
        }

        private static void DrawTile(TileDebugInfo tile, MercatorPivot pivot)
        {
            var b = tile.Coord.BoundsMeters();
            var sw = pivot.WorldFromMercator(new double2(b.x, b.y));
            var ne = pivot.WorldFromMercator(new double2(b.z, b.w));
            var center = new Vector3((sw.x + ne.x) * 0.5f, 0f, (sw.z + ne.z) * 0.5f);
            var size = new Vector3(math.abs(ne.x - sw.x), 0.5f, math.abs(ne.z - sw.z));

            // Slightly different frame sizes keep coincident edges of different states from merging.
            if (tile.IsRendered)                                               { Gizmos.color = Color.green;   size *= 0.99f; }
            else if (tile.Residency == TileResidency.Loading || tile.IsQueued) { Gizmos.color = Color.yellow;  size *= 0.98f; }
            else if (tile.IsEmpty)                                             { Gizmos.color = Color.red;     size *= 0.97f; }
            else if (tile.IsAdmitted)                                          { Gizmos.color = Color.cyan;    size *= 0.96f; }
            else if (tile.Verdict == TileBudgetVerdict.Denied)                 { Gizmos.color = Color.magenta; size *= 0.95f; }
            else                                                                 Gizmos.color = Color.gray;
            Gizmos.DrawWireCube(center, size);
        }

#if UNITY_EDITOR
        private void DrawPanel(TileLifecycleManager manager, TileDebugSummary summary, long groundGridBytes)
        {
            var limits = manager.Budget;
            string text =
                $"Tiles: {summary.Rendered} rendered, {summary.Empty} empty, " +
                $"{summary.Loaded} loaded ({summary.Warm} warm, {Mb(summary.WarmMemory.GpuBytes)} GPU)\n" +
                $"Loads: {summary.Loading} running + {summary.Cancelling} cancelling / {manager.MaxConcurrentLoads} slots, " +
                $"{summary.Queued} queued\n" +
                $"Budget: {summary.Admitted} admitted, {summary.Denied} denied" +
                (summary.BudgetExceeded ? " — limit reached" : "") + "\n" +
                Pool("GPU", summary.ResidentMemory.GpuBytes, limits.GpuBytes, summary.InFlightMemory.GpuBytes) + "\n" +
                Pool("Heap", summary.ResidentMemory.HeapBytes, limits.HeapBytes, summary.InFlightMemory.HeapBytes) +
                (groundGridBytes > 0 ? $"\nGround grids (shared, outside budget): {Mb(groundGridBytes)}" : "");

            var content = new GUIContent(text);
            _panelStyle ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 12 };
            var size = _panelStyle.CalcSize(content);

            UnityEditor.Handles.BeginGUI();
            GUI.Label(new Rect(10f, 10f, size.x, size.y), content, _panelStyle);
            UnityEditor.Handles.EndGUI();
        }

        private static string Pool(string name, long residentBytes, long limitBytes, long inFlightBytes)
        {
            double percent = limitBytes > 0 ? 100.0 * residentBytes / limitBytes : 0.0;
            return $"{name}: {Mb(residentBytes)} / {Mb(limitBytes)} ({percent:0}%), in flight {Mb(inFlightBytes)}";
        }

        private static string Mb(long bytes) => $"{bytes / Megabyte:0.0} MB";
#endif
    }
}
