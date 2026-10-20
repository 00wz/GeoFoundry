using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Geometry.Earcut
{
    /// <summary>
    /// A node of the doubly linked list of polygon vertices. Lives in a <c>NativeList&lt;NativeEarcutNode&gt;</c>
    /// pool; <see cref="Prev"/>, <see cref="Next"/>, <see cref="PrevZ"/> and <see cref="NextZ"/> are indices into
    /// that pool (<see cref="NativeEarcut.Null"/> = -1 instead of a managed <c>null</c>).
    /// </summary>
    internal struct NativeEarcutNode
    {
        public int Index;   // index of the source point (dim=2 is hard-coded)
        public float X, Y;
        public int ZOrder;  // 0 = "not computed"
        public int Prev, Next;
        public int PrevZ, NextZ;
        public bool Steiner;
    }

    /// <summary>
    /// Burst- and Job System-compatible triangulation of a polygon with holes into triangle indices (hashed
    /// ear clipping, hole elimination by leftmost sort, self-intersection curing, split fallback: the earcut
    /// algorithm, see Third Party Notices.md).
    ///
    /// Robust to degenerate input without relying on managed exceptions as a safety net: in Burst, reading at
    /// index -1 or out of bounds would be undefined behavior rather than an exception, so every such place has an
    /// explicit check with an early return of zero triangles instead of crashing or reading invalid memory.
    /// </summary>
    internal static class NativeEarcut
    {
        private const int Null = -1;

        /// <summary>
        /// Triangulates a polygon into <paramref name="triangles"/> (cleared first). <paramref name="points"/>
        /// holds the exterior ring followed by all holes. <paramref name="holeIndices"/> holds the start index (in
        /// <paramref name="points"/>) of each hole ring; empty or default if there are no holes.
        /// </summary>
        /// <param name="scratchAllocator">
        /// Allocator for the internal node pool and scratch buffers. Defaults to <see cref="Allocator.Temp"/>, which
        /// is also safe inside a job's Execute() (Temp is thread-local and lives for the duration of the call).
        /// </param>
        public static void Triangulate(
            NativeArray<float2> points, NativeArray<int> holeIndices,
            NativeList<int> triangles, Allocator scratchAllocator = Allocator.Temp)
        {
            triangles.Clear();
            if (points.Length == 0) return;

            var pool = new NativeList<NativeEarcutNode>(points.Length + 8, scratchAllocator);
            try
            {
                int hasHoles = holeIndices.Length;
                int outerLen = hasHoles > 0 ? holeIndices[0] : points.Length;
                outerLen = math.clamp(outerLen, 0, points.Length); // guard against garbage holeIndices

                int outerNode = LinkedList(points, 0, outerLen, true, pool);
                // Empty exterior ring: nothing to triangulate. Checked before reading the node.
                if (outerNode == Null) return;

                if (hasHoles > 0) outerNode = EliminateHoles(points, holeIndices, outerNode, pool, scratchAllocator);
                if (outerNode == Null) return;

                float minX = 0, minY = 0, maxX = 0, maxY = 0, size = 0;

                // As in earcut: the bbox covers only the exterior ring, while the threshold uses the TOTAL point
                // count (exterior + all holes).
                if (points.Length > 80)
                {
                    minX = maxX = points[0].x;
                    minY = maxY = points[0].y;
                    for (int i = 1; i < outerLen; i++)
                    {
                        float x = points[i].x, y = points[i].y;
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                    size = math.max(maxX - minX, maxY - minY);
                }

                EarcutLinked(outerNode, triangles, minX, minY, size, 0, pool);
            }
            finally
            {
                pool.Dispose();
            }
        }

        // ---- Node pool primitives ---------------------------------------------

        private static int InsertNode(int i, float x, float y, int last, NativeList<NativeEarcutNode> pool)
        {
            int p = pool.Length;
            pool.Add(new NativeEarcutNode
            {
                Index = i, X = x, Y = y, ZOrder = 0,
                Prev = Null, Next = Null, PrevZ = Null, NextZ = Null, Steiner = false,
            });

            if (last == Null)
            {
                pool.ElementAt(p).Prev = p;
                pool.ElementAt(p).Next = p;
            }
            else
            {
                pool.ElementAt(p).Next = pool[last].Next;
                pool.ElementAt(p).Prev = last;
                pool.ElementAt(pool[last].Next).Prev = p;
                pool.ElementAt(last).Next = p;
            }
            return p;
        }

        private static void RemoveNode(NativeList<NativeEarcutNode> pool, int p)
        {
            int pNext = pool[p].Next, pPrev = pool[p].Prev;
            pool.ElementAt(pNext).Prev = pPrev;
            pool.ElementAt(pPrev).Next = pNext;

            int pPrevZ = pool[p].PrevZ, pNextZ = pool[p].NextZ;
            if (pPrevZ != Null) pool.ElementAt(pPrevZ).NextZ = pNextZ;
            if (pNextZ != Null) pool.ElementAt(pNextZ).PrevZ = pPrevZ;
        }

        private static bool Equals2(NativeList<NativeEarcutNode> pool, int a, int b) =>
            pool[a].X == pool[b].X && pool[a].Y == pool[b].Y;

        // ---- Linked list construction ------------------------------------------

        private static float SignedArea(NativeArray<float2> points, int start, int end)
        {
            float sum = 0;
            int j = end - 1;
            for (int i = start; i < end; i++)
            {
                sum += (points[j].x - points[i].x) * (points[i].y + points[j].y);
                j = i;
            }
            return sum;
        }

        /// <summary>Creates a circular doubly linked list of the ring's points in the given winding order.</summary>
        private static int LinkedList(NativeArray<float2> points, int start, int end, bool clockwise, NativeList<NativeEarcutNode> pool)
        {
            int last = Null;

            if (clockwise == (SignedArea(points, start, end) > 0))
            {
                for (int i = start; i < end; i++) last = InsertNode(i, points[i].x, points[i].y, last, pool);
            }
            else
            {
                for (int i = end - 1; i >= start; i--) last = InsertNode(i, points[i].x, points[i].y, last, pool);
            }

            if (last != Null && Equals2(pool, last, pool[last].Next))
            {
                RemoveNode(pool, last);
                last = pool[last].Next;
            }

            return last;
        }

        private static int FilterPoints(NativeList<NativeEarcutNode> pool, int start, int end)
        {
            if (start == Null) return start;
            if (end == Null) end = start;

            int p = start;
            bool again = true;
            do
            {
                again = false;
                int pPrev = pool[p].Prev, pNext = pool[p].Next;
                if (!pool[p].Steiner && (Equals2(pool, p, pNext) || Area(pool, pPrev, p, pNext) == 0))
                {
                    RemoveNode(pool, p);
                    p = end = pool[p].Prev;
                    if (p == pool[p].Next) return Null;
                    again = true;
                }
                else
                {
                    p = pool[p].Next;
                }
            } while (again || p != end);

            return end;
        }

        private static int SplitPolygon(NativeList<NativeEarcutNode> pool, int a, int b)
        {
            int a2 = pool.Length;
            pool.Add(new NativeEarcutNode
            {
                Index = pool[a].Index, X = pool[a].X, Y = pool[a].Y, ZOrder = 0,
                Prev = Null, Next = Null, PrevZ = Null, NextZ = Null, Steiner = false,
            });
            int b2 = pool.Length;
            pool.Add(new NativeEarcutNode
            {
                Index = pool[b].Index, X = pool[b].X, Y = pool[b].Y, ZOrder = 0,
                Prev = Null, Next = Null, PrevZ = Null, NextZ = Null, Steiner = false,
            });

            int an = pool[a].Next;
            int bp = pool[b].Prev;

            pool.ElementAt(a).Next = b;
            pool.ElementAt(b).Prev = a;

            pool.ElementAt(a2).Next = an;
            pool.ElementAt(an).Prev = a2;

            pool.ElementAt(b2).Next = a2;
            pool.ElementAt(a2).Prev = b2;

            pool.ElementAt(bp).Next = b2;
            pool.ElementAt(b2).Prev = bp;

            return b2;
        }

        // ---- Hole elimination ---------------------------------------------------

        private struct LeftmostComparer : IComparer<int>
        {
            public NativeList<NativeEarcutNode> Pool;
            public int Compare(int a, int b) => Pool[a].X.CompareTo(Pool[b].X);
        }

        private static int EliminateHoles(
            NativeArray<float2> points, NativeArray<int> holeIndices, int outerNode,
            NativeList<NativeEarcutNode> pool, Allocator scratchAllocator)
        {
            int len = holeIndices.Length;
            var queue = new NativeList<int>(len, scratchAllocator);

            for (int i = 0; i < len; i++)
            {
                int start = holeIndices[i];
                int end = i < len - 1 ? holeIndices[i + 1] : points.Length;
                start = math.clamp(start, 0, points.Length);
                end = math.clamp(end, 0, points.Length);
                if (end <= start) continue; // degenerate or empty hole: skip instead of crashing

                int list = LinkedList(points, start, end, false, pool);
                if (list == Null) continue;

                if (list == pool[list].Next) pool.ElementAt(list).Steiner = true;
                queue.Add(GetLeftmost(pool, list));
            }

            queue.Sort(new LeftmostComparer { Pool = pool });

            for (int i = 0; i < queue.Length; i++)
            {
                EliminateHole(pool, queue[i], outerNode);
                // As in earcut: EliminateHole does not return a new outerNode; we continue with the OLD one (it
                // stays part of the merged ring, since SplitPolygon only inserts a bridge and removes no nodes).
                outerNode = FilterPoints(pool, outerNode, pool[outerNode].Next);
            }

            queue.Dispose();
            return outerNode;
        }

        private static void EliminateHole(NativeList<NativeEarcutNode> pool, int hole, int outerNode)
        {
            int bridge = FindHoleBridge(pool, hole, outerNode);
            if (bridge != Null)
            {
                int b = SplitPolygon(pool, bridge, hole);
                FilterPoints(pool, b, pool[b].Next);
            }
        }

        private static int FindHoleBridge(NativeList<NativeEarcutNode> pool, int hole, int outerNode)
        {
            int p = outerNode;
            float hx = pool[hole].X, hy = pool[hole].Y;
            float qx = float.MinValue;
            int m = Null;

            do
            {
                int pNext = pool[p].Next;
                float py = pool[p].Y, pnY = pool[pNext].Y;
                if (hy <= py && hy >= pnY && pnY != py)
                {
                    float px = pool[p].X, pnX = pool[pNext].X;
                    float x = px + (hy - py) * (pnX - px) / (pnY - py);
                    if (x <= hx && x > qx)
                    {
                        qx = x;
                        if (x == hx)
                        {
                            if (hy == py) return p;
                            if (hy == pnY) return pNext;
                        }
                        m = px < pnX ? p : pNext;
                    }
                }
                p = pool[p].Next;
            } while (p != outerNode);

            if (m == Null) return Null;
            if (hx == qx) return pool[m].Prev;

            int stop = m;
            float mx = pool[m].X, my = pool[m].Y;
            float tanMin = float.MaxValue;

            p = pool[m].Next;
            while (p != stop)
            {
                float px2 = pool[p].X, py2 = pool[p].Y;
                if (hx >= px2 && px2 >= mx && hx != px2 &&
                    PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, px2, py2))
                {
                    float tan = math.abs(hy - py2) / (hx - px2);
                    // The tie-break deliberately compares against the LIVE pool[m].X (m may change inside the loop)
                    // rather than the captured mx, as in earcut (m.x is read live; mx/my are locals captured only
                    // for the first check).
                    if ((tan < tanMin || (tan == tanMin && px2 > pool[m].X)) && LocallyInside(pool, p, hole))
                    {
                        m = p;
                        tanMin = tan;
                    }
                }
                p = pool[p].Next;
            }
            return m;
        }

        private static int GetLeftmost(NativeList<NativeEarcutNode> pool, int start)
        {
            int p = start, leftmost = start;
            do
            {
                if (pool[p].X < pool[leftmost].X) leftmost = p;
                p = pool[p].Next;
            } while (p != start);
            return leftmost;
        }

        // ---- Main ear-clipping loop (3 passes) -----------------------------------

        private static void EarcutLinked(
            int ear, NativeList<int> triangles, float minX, float minY, float size, int pass,
            NativeList<NativeEarcutNode> pool)
        {
            if (ear == Null) return;

            if (pass == 0 && size > 0) IndexCurve(pool, ear, minX, minY, size);

            int stop = ear;
            while (pool[ear].Prev != pool[ear].Next)
            {
                int prev = pool[ear].Prev;
                int next = pool[ear].Next;

                if (size > 0 ? IsEarHashed(pool, ear, minX, minY, size) : IsEar(pool, ear))
                {
                    triangles.Add(pool[prev].Index);
                    triangles.Add(pool[next].Index);
                    triangles.Add(pool[ear].Index);

                    RemoveNode(pool, ear);

                    // skip the next vertex: fewer sliver triangles (as in earcut)
                    ear = pool[next].Next;
                    stop = pool[next].Next;
                    continue;
                }

                ear = next;

                if (ear == stop)
                {
                    if (pass == 0)
                    {
                        int filtered = FilterPoints(pool, ear, Null);
                        EarcutLinked(filtered, triangles, minX, minY, size, 1, pool);
                    }
                    else if (pass == 1)
                    {
                        ear = CureLocalIntersections(pool, ear, triangles);
                        EarcutLinked(ear, triangles, minX, minY, size, 2, pool);
                    }
                    else if (pass == 2)
                    {
                        SplitEarcut(pool, ear, triangles, minX, minY, size);
                    }
                    break;
                }
            }
        }

        private static bool IsEar(NativeList<NativeEarcutNode> pool, int ear)
        {
            int a = pool[ear].Prev, b = ear, c = pool[ear].Next;
            if (Area(pool, a, b, c) >= 0) return false;

            int p = pool[c].Next;
            while (p != a)
            {
                if (PointInTriangle(pool[a].X, pool[a].Y, pool[b].X, pool[b].Y, pool[c].X, pool[c].Y, pool[p].X, pool[p].Y) &&
                    Area(pool, pool[p].Prev, p, pool[p].Next) >= 0) return false;
                p = pool[p].Next;
            }
            return true;
        }

        private static bool IsEarHashed(NativeList<NativeEarcutNode> pool, int ear, float minX, float minY, float size)
        {
            int a = pool[ear].Prev, b = ear, c = pool[ear].Next;
            if (Area(pool, a, b, c) >= 0) return false;

            float ax = pool[a].X, ay = pool[a].Y;
            float bx = pool[b].X, by = pool[b].Y;
            float cx = pool[c].X, cy = pool[c].Y;

            float minTX = ax < bx ? (ax < cx ? ax : cx) : (bx < cx ? bx : cx);
            float minTY = ay < by ? (ay < cy ? ay : cy) : (by < cy ? by : cy);
            float maxTX = ax > bx ? (ax > cx ? ax : cx) : (bx > cx ? bx : cx);
            float maxTY = ay > by ? (ay > cy ? ay : cy) : (by > cy ? by : cy);

            int minZ = ZOrder(minTX, minTY, minX, minY, size);
            int maxZ = ZOrder(maxTX, maxTY, minX, minY, size);

            int p = pool[ear].NextZ;
            while (p != Null && pool[p].ZOrder <= maxZ)
            {
                if (p != a && p != c &&
                    PointInTriangle(ax, ay, bx, by, cx, cy, pool[p].X, pool[p].Y) &&
                    Area(pool, pool[p].Prev, p, pool[p].Next) >= 0) return false;
                p = pool[p].NextZ;
            }

            p = pool[ear].PrevZ;
            while (p != Null && pool[p].ZOrder >= minZ)
            {
                if (p != a && p != c &&
                    PointInTriangle(ax, ay, bx, by, cx, cy, pool[p].X, pool[p].Y) &&
                    Area(pool, pool[p].Prev, p, pool[p].Next) >= 0) return false;
                p = pool[p].PrevZ;
            }

            return true;
        }

        private static void IndexCurve(NativeList<NativeEarcutNode> pool, int start, float minX, float minY, float size)
        {
            int p = start;
            do
            {
                if (pool[p].ZOrder == 0) pool.ElementAt(p).ZOrder = ZOrder(pool[p].X, pool[p].Y, minX, minY, size);
                pool.ElementAt(p).PrevZ = pool[p].Prev;
                pool.ElementAt(p).NextZ = pool[p].Next;
                p = pool[p].Next;
            } while (p != start);

            int pPrevZ = pool[p].PrevZ; // here p == start
            pool.ElementAt(pPrevZ).NextZ = Null;
            pool.ElementAt(p).PrevZ = Null;

            // The return value of SortLinked is deliberately ignored, as in earcut: only the side effect (rebuilt
            // PrevZ/NextZ) is needed, and the original start node remains a valid member of the re-sorted Z chain.
            SortLinked(pool, p);
        }

        private static int SortLinked(NativeList<NativeEarcutNode> pool, int list)
        {
            int inSize = 1;
            int numMerges;

            do
            {
                int p = list;
                list = Null;
                int tail = Null;
                numMerges = 0;

                while (p != Null)
                {
                    numMerges++;
                    int q = p;
                    int pSize = 0;
                    for (int i = 0; i < inSize; i++)
                    {
                        pSize++;
                        q = pool[q].NextZ;
                        if (q == Null) break;
                    }
                    int qSize = inSize;

                    while (pSize > 0 || (qSize > 0 && q != Null))
                    {
                        int e;
                        if (pSize != 0 && (qSize == 0 || q == Null || pool[p].ZOrder <= pool[q].ZOrder))
                        {
                            e = p; p = pool[p].NextZ; pSize--;
                        }
                        else
                        {
                            e = q; q = pool[q].NextZ; qSize--;
                        }

                        if (tail != Null) pool.ElementAt(tail).NextZ = e;
                        else list = e;

                        pool.ElementAt(e).PrevZ = tail;
                        tail = e;
                    }
                    p = q;
                }

                if (tail == Null) return list; // guard: unreachable for valid non-empty input
                pool.ElementAt(tail).NextZ = Null;
                inSize *= 2;
            } while (numMerges > 1);

            return list;
        }

        private static int ZOrder(float x, float y, float minX, float minY, float size)
        {
            x = 32767 * (x - minX) / size;
            y = 32767 * (y - minY) / size;

            x = ((int)x | ((int)x << 8)) & 0x00FF00FF;
            x = ((int)x | ((int)x << 4)) & 0x0F0F0F0F;
            x = ((int)x | ((int)x << 2)) & 0x33333333;
            x = ((int)x | ((int)x << 1)) & 0x55555555;

            y = ((int)y | ((int)y << 8)) & 0x00FF00FF;
            y = ((int)y | ((int)y << 4)) & 0x0F0F0F0F;
            y = ((int)y | ((int)y << 2)) & 0x33333333;
            y = ((int)y | ((int)y << 1)) & 0x55555555;

            return (int)x | ((int)y << 1);
        }

        // ---- Self-intersection curing + split fallback ---------------------------

        private static int CureLocalIntersections(NativeList<NativeEarcutNode> pool, int start, NativeList<int> triangles)
        {
            int p = start;
            do
            {
                int a = pool[p].Prev;
                int pNext = pool[p].Next;
                int b = pool[pNext].Next;

                if (!Equals2(pool, a, b) && Intersects(pool, a, p, pNext, b) &&
                    LocallyInside(pool, a, b) && LocallyInside(pool, b, a))
                {
                    triangles.Add(pool[a].Index);
                    triangles.Add(pool[p].Index);
                    triangles.Add(pool[b].Index);

                    RemoveNode(pool, p);
                    RemoveNode(pool, pNext);

                    p = start = b;
                }
                p = pool[p].Next;
            } while (p != start);

            return p;
        }

        private static void SplitEarcut(
            NativeList<NativeEarcutNode> pool, int start, NativeList<int> triangles,
            float minX, float minY, float size)
        {
            int a = start;
            do
            {
                int b = pool[pool[a].Next].Next;
                int aPrev = pool[a].Prev; // 'a' does not change in this loop, so caching is safe

                while (b != aPrev)
                {
                    if (pool[a].Index != pool[b].Index && IsValidDiagonal(pool, a, b))
                    {
                        int c = SplitPolygon(pool, a, b);
                        a = FilterPoints(pool, a, pool[a].Next);
                        c = FilterPoints(pool, c, pool[c].Next);

                        EarcutLinked(a, triangles, minX, minY, size, 0, pool);
                        EarcutLinked(c, triangles, minX, minY, size, 0, pool);
                        return;
                    }
                    b = pool[b].Next;
                }
                a = pool[a].Next;
            } while (a != start);
        }

        private static bool IsValidDiagonal(NativeList<NativeEarcutNode> pool, int a, int b)
        {
            int bIdx = pool[b].Index;
            return pool[pool[a].Next].Index != bIdx && pool[pool[a].Prev].Index != bIdx &&
                   !IntersectsPolygon(pool, a, b) &&
                   LocallyInside(pool, a, b) && LocallyInside(pool, b, a) &&
                   MiddleInside(pool, a, b);
        }

        private static bool MiddleInside(NativeList<NativeEarcutNode> pool, int a, int b)
        {
            int p = a;
            bool inside = false;
            float px = (pool[a].X + pool[b].X) / 2f;
            float py = (pool[a].Y + pool[b].Y) / 2f;

            do
            {
                int pNext = pool[p].Next;
                float pY = pool[p].Y, pNextY = pool[pNext].Y, pX = pool[p].X, pNextX = pool[pNext].X;
                if (((pY > py) != (pNextY > py)) && pNextY != pY &&
                    (px < (pNextX - pX) * (py - pY) / (pNextY - pY) + pX)) inside = !inside;
                p = pool[p].Next;
            } while (p != a);

            return inside;
        }

        private static bool IntersectsPolygon(NativeList<NativeEarcutNode> pool, int a, int b)
        {
            int p = a;
            int aIdx = pool[a].Index, bIdx = pool[b].Index;
            do
            {
                int pNext = pool[p].Next;
                if (pool[p].Index != aIdx && pool[pNext].Index != aIdx &&
                    pool[p].Index != bIdx && pool[pNext].Index != bIdx &&
                    Intersects(pool, p, pNext, a, b)) return true;
                p = pool[p].Next;
            } while (p != a);
            return false;
        }

        private static bool Intersects(NativeList<NativeEarcutNode> pool, int p1, int q1, int p2, int q2)
        {
            if ((Equals2(pool, p1, q1) && Equals2(pool, p2, q2)) ||
                (Equals2(pool, p1, q2) && Equals2(pool, p2, q1))) return true;

            return (Area(pool, p1, q1, p2) > 0) != (Area(pool, p1, q1, q2) > 0) &&
                   (Area(pool, p2, q2, p1) > 0) != (Area(pool, p2, q2, q1) > 0);
        }

        private static bool LocallyInside(NativeList<NativeEarcutNode> pool, int a, int b)
        {
            int aPrev = pool[a].Prev, aNext = pool[a].Next;
            return Area(pool, aPrev, a, aNext) < 0
                ? Area(pool, a, b, aNext) >= 0 && Area(pool, a, aPrev, b) >= 0
                : Area(pool, a, b, aPrev) < 0 || Area(pool, a, aNext, b) < 0;
        }

        // ---- 2D geometry primitives -----------------------------------------------

        private static float Area(NativeList<NativeEarcutNode> pool, int p, int q, int r)
        {
            float py = pool[p].Y, px = pool[p].X;
            float qx = pool[q].X, qy = pool[q].Y;
            float rx = pool[r].X, ry = pool[r].Y;
            return (qy - py) * (rx - qx) - (qx - px) * (ry - qy);
        }

        private static bool PointInTriangle(
            float ax, float ay, float bx, float by, float cx, float cy, float px, float py) =>
            (cx - px) * (ay - py) - (ax - px) * (cy - py) >= 0 &&
            (ax - px) * (by - py) - (bx - px) * (ay - py) >= 0 &&
            (bx - px) * (cy - py) - (cx - px) * (by - py) >= 0;
    }
}
