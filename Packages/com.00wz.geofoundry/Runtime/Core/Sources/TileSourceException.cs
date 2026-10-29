using System;

namespace GeoFoundry.Core.Sources
{
    /// <summary>
    /// Final failure of a tile source to provide a tile, whatever the transport or storage: retries are exhausted, the
    /// server or storage refuses the request, the data is corrupt. Not used for cancellation or for missing data,
    /// which a source reports as a regular result where its contract allows it (see <see cref="IDemTileSource"/>).
    ///
    /// Consumers of sources (tile loading, the DEM cache) depend only on this type: it tells an external failure apart
    /// from a bug in the code. A source derives its own exception from it with the details of its transport, for
    /// example <c>GeoFoundry.TileIO.Net.TileDownloadException</c> with the URL and HTTP status.
    /// </summary>
    public class TileSourceException : Exception
    {
        /// <summary>Initializes a new instance.</summary>
        /// <param name="message">The error message.</param>
        /// <param name="inner">The underlying exception, if any.</param>
        public TileSourceException(string message, Exception inner = null)
            : base(message, inner)
        {
        }
    }
}
