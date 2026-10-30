using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// The maximum screen size of a tile, shared by the OpenMapTiles handlers that depend on the size at which tiles are
    /// shown: <see cref="OmtTextSettings"/> (label declutter tiers, see <see cref="OmtZoom.LabelTierRange"/>) and
    /// <see cref="OmtBuildingHandler"/> (the start of the building fade, see <see cref="OmtZoom.TileShownFromZoom"/>).
    /// </summary>
    /// <remarks>
    /// Handlers cannot query the tile selection, so the value must be kept equal to
    /// <c>GeoFoundryConfig.Lod.MaxTileScreenSizePixels</c> by hand. It is read when a tile is built: changing it does
    /// not affect tiles that are already loaded.
    /// </remarks>
    [CreateAssetMenu(menuName = "GeoFoundry/OpenMapTiles/Tile Screen Size", fileName = "OmtTileScreenSize")]
    public sealed class OmtTileScreenSize : ScriptableObject
    {
        [Tooltip("Must equal GeoFoundryConfig → Lod → Max Tile Screen Size Pixels: a tile is shown while its screen size " +
                 "is between half of this value and this value.")]
        [SerializeField, Min(1f)] private float _maxPixels = 1500f;

        /// <summary>Gets or sets the maximum screen size of a tile in pixels.</summary>
        public float MaxPixels
        {
            get => _maxPixels;
            set => _maxPixels = Mathf.Max(1f, value);
        }

        /// <summary>
        /// Returns <see cref="MaxPixels"/> of <paramref name="config"/>; without a config, 1024: tiles of zoom <c>z</c>
        /// are then taken to be shown from display zoom <c>z</c>.
        /// </summary>
        public static float PixelsOf(OmtTileScreenSize config) =>
            config != null ? config.MaxPixels : 2f * (float)OmtZoom.TilePixels;
    }
}
