using System;
using GeoFoundry.Core.Sources;

namespace GeoFoundry.TileIO.PmTiles
{
    /// <summary>An invalid archive, failed range read or unsupported PMTiles feature.</summary>
    public sealed class PmTilesException : TileSourceException
    {
        public PmTilesException(string message, Exception inner = null) : base(message, inner) { }
    }
}
