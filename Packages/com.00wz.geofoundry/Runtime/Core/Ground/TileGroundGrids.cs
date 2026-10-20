namespace GeoFoundry.Core.Ground
{
    /// <summary>
    /// Ground grids of a tile and its 8 neighbors, as named slots rather than a positional array, so that which
    /// neighbor is where is part of the type and not a convention about collection order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type does not guarantee that slots are valid: any slot may be <c>default(GroundGrid)</c>. A handler
    /// checks exactly the slots it reads (<see cref="GroundGrid.IsValid"/>) and throws if a grid it needs is
    /// missing.
    /// </para>
    /// <para>
    /// What <see cref="GeoFoundry.Tiles.TileBuilder"/> provides: <see cref="Center"/> always (a flat 1×1 grid
    /// when terrain elevation is off); all 8 neighbors when <c>GeoFoundryConfig.LoadNeighborGrids</c> is
    /// enabled, including those beyond a pole (there is no tile there, but there is a grid at the fallback
    /// height); otherwise all 8 are <c>default</c>. Failing to obtain any grid fails the tile build; it never
    /// leaves <c>default</c> in a slot.
    /// </para>
    /// <para>The grids are borrowed for the duration of the build; handlers do not dispose them.</para>
    /// </remarks>
    public readonly struct TileGroundGrids
    {
        /// <summary>The grid of the tile itself.</summary>
        public readonly GroundGrid Center;
        /// <summary>The grid of the northern neighbor (Y - 1).</summary>
        public readonly GroundGrid North;
        /// <summary>The grid of the southern neighbor (Y + 1).</summary>
        public readonly GroundGrid South;
        /// <summary>The grid of the eastern neighbor (X + 1).</summary>
        public readonly GroundGrid East;
        /// <summary>The grid of the western neighbor (X - 1).</summary>
        public readonly GroundGrid West;
        /// <summary>The grid of the north-eastern neighbor.</summary>
        public readonly GroundGrid NorthEast;
        /// <summary>The grid of the north-western neighbor.</summary>
        public readonly GroundGrid NorthWest;
        /// <summary>The grid of the south-eastern neighbor.</summary>
        public readonly GroundGrid SouthEast;
        /// <summary>The grid of the south-western neighbor.</summary>
        public readonly GroundGrid SouthWest;

        /// <summary>Initializes the set of grids.</summary>
        public TileGroundGrids(
            GroundGrid center, GroundGrid north, GroundGrid south, GroundGrid east, GroundGrid west,
            GroundGrid northEast, GroundGrid northWest, GroundGrid southEast, GroundGrid southWest)
        {
            Center = center;
            North = north;
            South = south;
            East = east;
            West = west;
            NorthEast = northEast;
            NorthWest = northWest;
            SouthEast = southEast;
            SouthWest = southWest;
        }
    }
}
