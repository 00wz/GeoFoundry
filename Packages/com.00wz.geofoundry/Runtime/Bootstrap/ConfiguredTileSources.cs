using System;
using System.IO;
using GeoFoundry.Core.Sources;
using GeoFoundry.TileIO.Net;
using GeoFoundry.TileIO.PmTiles;
using GeoFoundry.TileIO.Sources;

namespace GeoFoundry.Bootstrap
{
    /// <summary>Owns a config's source pair and, in remote mode, their shared downloader.</summary>
    internal sealed class ConfiguredTileSources : IDisposable
    {
        public IVectorTileSource Vector { get; private set; }
        public IDemTileSource Dem { get; private set; }
        TileDownloader _downloader;
        bool _disposed;

        public static ConfiguredTileSources Create(GeoFoundryConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var result = new ConfiguredTileSources();
            try
            {
                switch (config.SourceMode)
                {
                    case TileSourceMode.LocalPmTiles:
                        if (string.IsNullOrWhiteSpace(config.VectorArchivePath))
                            throw new ArgumentException("Local PMTiles mode requires VectorArchivePath in Config.");
                        result.Vector = new PmTilesVectorTileSource(ResolveArchivePath(config.VectorArchivePath));
                        if (config.EnableRelief && !string.IsNullOrWhiteSpace(config.DemArchivePath))
                            result.Dem = new PmTilesDemTileSource(ResolveArchivePath(config.DemArchivePath), config.TileSizePixels);
                        break;
                    case TileSourceMode.RemoteHttp:
                        if (string.IsNullOrWhiteSpace(config.VectorTilesEndpointTemplate))
                            throw new ArgumentException("Remote HTTP mode requires VectorTilesEndpointTemplate in Config.");
                        result._downloader = new TileDownloader(config.MaxConcurrentHttpRequests);
                        result.Vector = new HttpVectorTileSource(config.VectorTilesEndpointTemplate, result._downloader);
                        if (config.EnableRelief && !string.IsNullOrWhiteSpace(config.DemTilesEndpointTemplate))
                            result.Dem = new HttpDemTileSource(config.DemTilesEndpointTemplate, result._downloader, config.TileSizePixels);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(config.SourceMode), config.SourceMode, "Unknown tile source mode.");
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal static string ResolveArchivePath(string path)
        {
            if (Path.IsPathRooted(path) || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
                return path;
            return PmTilesByteSources.StreamingAsset(path);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { (Vector as IDisposable)?.Dispose(); }
            finally
            {
                try { (Dem as IDisposable)?.Dispose(); }
                finally { _downloader?.Dispose(); }
            }
        }
    }
}
