using GeoFoundry.Core.Coordinates;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Core.Ground
{
    /// <summary>
    /// Read-only view of the ground mesh of one tile: bounds, grid resolution, heights and optional normals.
    /// Used by <see cref="NativeGroundProjector"/> to sample the height at any point in normalized [0, 1] tile
    /// coordinates by barycentric interpolation over the cell triangle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The struct does not own its native arrays; the ground grid provider that created it disposes them.
    /// </para>
    /// <para>
    /// The grid is pivot-independent: methods that convert to Unity coordinates (<see cref="SampleWorld"/>,
    /// <see cref="WorldFromGridCorner"/>, <see cref="SampleNormal"/>) take the <see cref="MercatorPivot"/> as a
    /// parameter, because the same <see cref="TileCoord"/> may be sampled as a neighbor in different frames
    /// depending on which tile is being built.
    /// </para>
    /// <para>
    /// Cell triangulation matches <see cref="GeoFoundry.Ground.TerrainMeshBuilder"/>:
    /// tri0 = (i00, i01, i11); tri1 = (i00, i11, i10), always the same diagonal (no checkerboard), so sampled
    /// heights lie exactly on the terrain surface.
    /// </para>
    /// </remarks>
    public readonly struct GroundGrid
    {
        /// <summary>Number of grid cells along one side of the tile.</summary>
        public readonly int SegmentsPerSide;

        /// <summary>
        /// Vertex heights in meters, row-major, <see cref="VertsPerSide"/>² values; index is
        /// <c>j * VertsPerSide + i</c>.
        /// </summary>
        public readonly NativeArray<float> Heights;

        /// <summary>
        /// Averaged per-vertex normals, the same values as in the terrain mesh. May be not created
        /// (<see cref="NativeArray{T}.IsCreated"/> is false); <see cref="SampleVertexNormal"/> then falls back to
        /// the flat cell normal.
        /// </summary>
        public readonly NativeArray<Vector3> Normals;

        /// <summary>Tile bounds in Mercator meters: (minX, minY, maxX, maxY).</summary>
        public readonly double4 BoundsMeters; // minX, minY, maxX, maxY

        /// <summary>Gets the number of vertices along one side: <see cref="SegmentsPerSide"/> + 1.</summary>
        public int VertsPerSide => SegmentsPerSide + 1;

        /// <summary>
        /// Gets whether the grid is set (<see cref="Heights"/> is created). <c>default(GroundGrid)</c> is an empty
        /// slot, for example a neighbor that was not requested in <see cref="TileGroundGrids"/>.
        /// <see cref="Normals"/> do not affect validity.
        /// </summary>
        public bool IsValid => Heights.IsCreated;

        /// <summary>Initializes a grid without normals.</summary>
        public GroundGrid(int segmentsPerSide, NativeArray<float> heights, double4 bounds)
            : this(segmentsPerSide, heights, default, bounds) { }

        /// <summary>Initializes a grid. The arrays are referenced, not copied, and stay owned by the caller.</summary>
        public GroundGrid(int segmentsPerSide, NativeArray<float> heights, NativeArray<Vector3> normals,
                          double4 bounds)
        {
            SegmentsPerSide = segmentsPerSide;
            Heights = heights;
            Normals = normals;
            BoundsMeters = bounds;
        }

        /// <summary>Returns the height at grid vertex (i, j), where i, j ∈ [0, SegmentsPerSide].</summary>
        public float HeightAt(int i, int j) => Heights[j * VertsPerSide + i];

        /// <summary>
        /// Returns the Unity position, relative to <paramref name="frame"/>, of the normalized tile point
        /// <paramref name="uv"/> ∈ [0, 1]². The height is interpolated over the same cell triangle as in the
        /// terrain mesh, so the point lies exactly on the terrain surface.
        /// </summary>
        public Vector3 SampleWorld(float2 uv, MercatorPivot frame)
        {
            double mx = math.lerp(BoundsMeters.x, BoundsMeters.z, uv.x);
            double my = math.lerp(BoundsMeters.y, BoundsMeters.w, uv.y);
            float h = SampleHeightAtUv(uv);
            var w = frame.WorldFromMercator(new double2(mx, my), h);
            return new Vector3(w.x, w.y, w.z);
        }

        /// <summary>Returns the interpolated height at <paramref name="uv"/> ∈ [0, 1]² without world conversion.</summary>
        public float SampleHeightAtUv(float2 uv)
        {
            UnpackCell(uv, out int i, out int j, out float lx, out float ly);
            return SampleHeightInCell(Heights, VertsPerSide, i, j, lx, ly);
        }

        /// <summary>
        /// Returns the interpolated height at (<paramref name="u"/>, <paramref name="v"/>) ∈ [0, 1]² of a raw
        /// height array, without constructing a <see cref="GroundGrid"/>. Uses the same math as the instance
        /// overload.
        /// </summary>
        public static float SampleHeightAtUv(NativeArray<float> heights, int vertsPerSide, float u, float v)
        {
            int segments = vertsPerSide - 1;
            float fx = math.clamp(u, 0f, 1f) * segments;
            float fy = math.clamp(v, 0f, 1f) * segments;
            int i = math.clamp((int)math.floor(fx), 0, segments - 1);
            int j = math.clamp((int)math.floor(fy), 0, segments - 1);
            return SampleHeightInCell(heights, vertsPerSide, i, j, fx - i, fy - j);
        }

        /// <summary>
        /// Splits a UV point into the cell (i, j) and the local coordinates lx, ly ∈ [0, 1] within it.
        /// </summary>
        private void UnpackCell(float2 uv, out int i, out int j, out float lx, out float ly)
        {
            int N = SegmentsPerSide;
            float fx = math.clamp(uv.x, 0f, 1f) * N;
            float fy = math.clamp(uv.y, 0f, 1f) * N;
            i = math.clamp((int)math.floor(fx), 0, N - 1);
            j = math.clamp((int)math.floor(fy), 0, N - 1);
            lx = fx - i;
            ly = fy - j;
        }

        /// <summary>
        /// Barycentric interpolation inside cell (i, j) with the triangulation of
        /// <see cref="GeoFoundry.Ground.TerrainMeshBuilder"/>:
        ///  tri0 (lx ≤ ly): (i,j)→(0,0), (i,j+1)→(0,1), (i+1,j+1)→(1,1).
        ///  tri1 (lx > ly): (i,j)→(0,0), (i+1,j+1)→(1,1), (i+1,j)→(1,0).
        /// The single implementation of height sampling in the package.
        /// </summary>
        private static float SampleHeightInCell(NativeArray<float> heights, int vps, int i, int j, float lx, float ly)
        {
            float h00 = heights[j * vps + i];
            float h10 = heights[j * vps + (i + 1)];
            float h01 = heights[(j + 1) * vps + i];
            float h11 = heights[(j + 1) * vps + (i + 1)];
            if (lx <= ly)
                return (1f - ly) * h00 + (ly - lx) * h01 + lx * h11;
            else
                return (1f - lx) * h00 + ly * h11 + (lx - ly) * h10;
        }

        /// <summary>
        /// Returns the Unity position, relative to <paramref name="frame"/>, of grid vertex (i, j). Used to compute
        /// the normal of the ground triangle a vertex is projected onto.
        /// </summary>
        public Vector3 WorldFromGridCorner(int i, int j, MercatorPivot frame)
        {
            double u = (double)i / SegmentsPerSide;
            double v = (double)j / SegmentsPerSide;
            double mx = math.lerp(BoundsMeters.x, BoundsMeters.z, u);
            double my = math.lerp(BoundsMeters.y, BoundsMeters.w, v);
            var w = frame.WorldFromMercator(new double2(mx, my), HeightAt(i, j));
            return new Vector3(w.x, w.y, w.z);
        }

        /// <summary>
        /// Returns the flat normal of the ground triangle containing <paramref name="uv"/>, always pointing to +Y.
        /// Handlers use it to shade surfaces so that lighting matches the terrain mesh.
        /// </summary>
        public Vector3 SampleNormal(float2 uv, MercatorPivot frame)
        {
            UnpackCell(uv, out int i, out int j, out float lx, out float ly);
            Vector3 w0, w1, w2;
            if (lx <= ly)
            {
                w0 = WorldFromGridCorner(i, j, frame);
                w1 = WorldFromGridCorner(i, j + 1, frame);
                w2 = WorldFromGridCorner(i + 1, j + 1, frame);
            }
            else
            {
                w0 = WorldFromGridCorner(i, j, frame);
                w1 = WorldFromGridCorner(i + 1, j + 1, frame);
                w2 = WorldFromGridCorner(i + 1, j, frame);
            }
            var n = Vector3.Cross(w1 - w0, w2 - w0).normalized;
            if (n.y < 0) n = -n;
            return n;
        }

        /// <summary>
        /// Returns the smooth normal at <paramref name="uv"/>: barycentric interpolation of the three vertex
        /// normals of the cell triangle, the same way the rasterizer interpolates terrain mesh normals, so shading
        /// matches the terrain. If <see cref="Normals"/> is not created, falls back to the flat
        /// <see cref="SampleNormal"/>; <paramref name="frame"/> is needed only for that fallback.
        /// </summary>
        public Vector3 SampleVertexNormal(float2 uv, MercatorPivot frame)
        {
            if (!Normals.IsCreated) return SampleNormal(uv, frame);

            UnpackCell(uv, out int i, out int j, out float lx, out float ly);
            int vps = VertsPerSide;
            Vector3 n00 = Normals[j * vps + i];
            Vector3 n10 = Normals[j * vps + (i + 1)];
            Vector3 n01 = Normals[(j + 1) * vps + i];
            Vector3 n11 = Normals[(j + 1) * vps + (i + 1)];

            // Same triangulation and barycentric weights as in SampleHeightInCell.
            //  tri0 (lx ≤ ly): V0=(i,j)→(0,0), V1=(i,j+1)→(0,1), V2=(i+1,j+1)→(1,1)
            //  tri1 (lx > ly): V0=(i,j)→(0,0), V1=(i+1,j+1)→(1,1), V2=(i+1,j)→(1,0)
            Vector3 n;
            if (lx <= ly)
                n = (1f - ly) * n00 + (ly - lx) * n01 + lx * n11;
            else
                n = (1f - lx) * n00 + ly * n11 + (lx - ly) * n10;

            // Linear interpolation shortens the normal: renormalize. Terrain normals always point up.
            var nz = n.normalized;
            if (nz.y < 0f) nz = -nz;
            return nz;
        }

        /// <summary>
        /// Writes into <paramref name="outTri"/> the vertices of one of the two ground triangles of cell
        /// (<paramref name="qx"/>, <paramref name="qy"/>), <paramref name="triIndex"/> ∈ {0, 1}. The triangle is
        /// counter-clockwise in normalized [0, 1] tile coordinates.
        /// </summary>
        public void GetCellTriangle(int qx, int qy, int triIndex, float2[] outTri)
        {
            float n = SegmentsPerSide;
            float qSize = 1f / n;
            float x0 = qx * qSize, y0 = qy * qSize;
            float x1 = x0 + qSize, y1 = y0 + qSize;

            // Match TerrainMeshBuilder triangulation:
            //  tri0: (i00, i01, i11) = (x0,y0), (x0,y1), (x1,y1).
            //  tri1: (i00, i11, i10) = (x0,y0), (x1,y1), (x1,y0).
            // The winding is defined relative to the cross-product direction; the visual orientation depends on
            // the side the plane is viewed from.
            if (triIndex == 0)
            {
                outTri[0] = new float2(x0, y0);
                outTri[1] = new float2(x0, y1);
                outTri[2] = new float2(x1, y1);
            }
            else
            {
                outTri[0] = new float2(x0, y0);
                outTri[1] = new float2(x1, y1);
                outTri[2] = new float2(x1, y0);
            }
        }

        /// <summary>
        /// Job- and Burst-compatible overload of <see cref="GetCellTriangle(int, int, int, float2[])"/> that
        /// returns the vertices through out parameters instead of a managed array.
        /// </summary>
        public void GetCellTriangle(int qx, int qy, int triIndex, out float2 t0, out float2 t1, out float2 t2)
        {
            float n = SegmentsPerSide;
            float qSize = 1f / n;
            float x0 = qx * qSize, y0 = qy * qSize;
            float x1 = x0 + qSize, y1 = y0 + qSize;

            t0 = new float2(x0, y0);
            if (triIndex == 0)
            {
                t1 = new float2(x0, y1);
                t2 = new float2(x1, y1);
            }
            else
            {
                t1 = new float2(x1, y1);
                t2 = new float2(x1, y0);
            }
        }
    }
}
