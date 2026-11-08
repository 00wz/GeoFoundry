using UnityEngine;

namespace GeoFoundry.Bootstrap
{
    /// <summary>Selects one transport family for both vector and elevation tiles.</summary>
    public enum TileSourceMode
    {
        [InspectorName("Local PMTiles")] LocalPmTiles = 0,
        [InspectorName("Remote HTTP")] RemoteHttp = 1,
    }
}
