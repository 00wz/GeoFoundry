using System;

namespace GeoFoundry.Core.Sources
{
    /// <summary>
    /// Final failure to download a tile, after retries are exhausted or because of a non-retryable HTTP status
    /// (4xx).
    /// </summary>
    public sealed class TileDownloadException : Exception
    {
        /// <summary>Gets the URL of the failed request.</summary>
        public string Url { get; }

        /// <summary>
        /// Gets the HTTP status code of the last attempt (<c>UnityWebRequest.responseCode</c>); not a valid status if
        /// no response was received.
        /// </summary>
        public long HttpStatus { get; }

        /// <summary>Initializes a new instance.</summary>
        /// <param name="url">The URL of the failed request.</param>
        /// <param name="httpStatus">The HTTP status code of the last attempt.</param>
        /// <param name="message">The error message.</param>
        /// <param name="inner">The underlying exception, if any.</param>
        public TileDownloadException(string url, long httpStatus, string message, Exception inner = null)
            : base(message, inner)
        {
            Url = url;
            HttpStatus = httpStatus;
        }
    }
}
