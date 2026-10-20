using Unity.Mathematics;

namespace GeoFoundry.Core.Coordinates
{
    /// <summary>
    /// Floating-origin pivot: an anchor point in Web Mercator meters stored in double precision, used to convert
    /// Mercator coordinates to single-precision Unity scene coordinates and back.
    /// </summary>
    /// <remarks>
    /// Axis mapping: Mercator X → Unity X, Mercator Y → Unity Z, height → Unity Y.
    /// When the point of interest moves farther than a threshold from the pivot, the pivot is moved there with
    /// <see cref="Rebase"/> and scene objects are repositioned relative to it, which keeps float precision
    /// high near the camera.
    /// </remarks>
    public struct MercatorPivot
    {
        private double2 _pivotMeters;

        /// <summary>Gets the pivot position in Web Mercator meters.</summary>
        public double2 PivotMeters => _pivotMeters;

        /// <summary>Initializes a pivot at <paramref name="pivotMeters"/> (Web Mercator meters).</summary>
        public MercatorPivot(double2 pivotMeters) { _pivotMeters = pivotMeters; }

        /// <summary>Creates a pivot at the given longitude/latitude (decimal degrees, WGS84).</summary>
        public static MercatorPivot FromLonLat(double lonDeg, double latDeg) =>
            new MercatorPivot(MercatorProjection.LonLatToMeters(lonDeg, latDeg));

        /// <summary>
        /// Converts a Mercator point to Unity coordinates relative to the pivot, with Y set to
        /// <paramref name="height"/>.
        /// </summary>
        public float3 WorldFromMercator(double2 mercator, float height = 0f)
        {
            var d = mercator - _pivotMeters;
            return new float3((float)d.x, height, (float)d.y);
        }

        /// <summary>Converts Unity coordinates relative to the pivot to Mercator meters. Y (height) is ignored.</summary>
        public double2 MercatorFromWorld(float3 world) =>
            _pivotMeters + new double2(world.x, world.z);

        /// <summary>Moves the pivot to <paramref name="newPivotMeters"/> (floating-origin rebase).</summary>
        public void Rebase(double2 newPivotMeters) { _pivotMeters = newPivotMeters; }

        /// <summary>
        /// Returns whether a rebase is needed, that is, whether <paramref name="targetMeters"/> is farther than
        /// <paramref name="thresholdMeters"/> from the pivot.
        /// </summary>
        public bool ShouldRebase(double2 targetMeters, double thresholdMeters)
        {
            var d = targetMeters - _pivotMeters;
            return math.lengthsq(d) > thresholdMeters * thresholdMeters;
        }

        public override string ToString() => $"MercatorPivot(meters={_pivotMeters.x:F3},{_pivotMeters.y:F3})";
    }
}
