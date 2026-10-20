using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// Trapped air while the cavity fills (2.5D): the part's ceiling map c(u, v) - the upper envelope of the part along W
    /// for a base pour - is flooded upside down. Air rises, so with e = -c air "flows downhill" in e; a priority-flood
    /// (Barnes, Lehman and Mulla 2014) from the outlets fills every pocket of e up to its spill level, and the filled
    /// depth is the air left in each pocket. Outlets: the outline, where air leaves along the parting plane at the plane's
    /// height, plus vents and risers (they drain the cell they sit on). A tilt about U is applied to the heights.
    /// Pure arithmetic, unit-tested on synthetic heightmaps (AirTrapsTests).
    /// </summary>
    public static class AirTraps
    {
        public sealed class Trap
        {
            public int Id;
            /// <summary>Trapped volume, in the heights' unit x cell area.</summary>
            public double Volume;
            public double Area;
            /// <summary>The highest ceiling point of the pocket (where a vent goes): cell and (untilted) ceiling height.</summary>
            public int HighI, HighJ;
            public double HighC;
            /// <summary>Deepest trapped air column (spill level minus ceiling at its highest point).</summary>
            public double Depth;
            public List<(int I, int J)> Cells = new List<(int, int)>();
        }

        public sealed class Input
        {
            /// <summary>Ceiling height per cell (NaN = no part in that column).</summary>
            public double[,] Ceiling;
            /// <summary>Cell size (for areas and volumes).</summary>
            public double Cell = 1;
            /// <summary>The parting plane's height: air leaves the cavity at the outline at this level.</summary>
            public double PlLevel;
            /// <summary>v coordinate of each column j (for the tilt). Null = 0.</summary>
            public double[] VOfColumn;
            /// <summary>Tilt about U in radians (positive raises +V).</summary>
            public double TiltRad;
            /// <summary>Cells drained by a vent or riser.</summary>
            public IEnumerable<(int I, int J)> Vents;
            /// <summary>Trapped depths below this are ignored (sampling noise).</summary>
            public double MinDepth = 1e-6;
        }

        /// <summary>Heights after the tilt: a column's height h becomes h cos(t) + v sin(t).</summary>
        public static double Tilted(double h, double v, double tilt) => h * Math.Cos(tilt) + v * Math.Sin(tilt);

        /// <summary>
        /// Priority-flood: the spill elevation of every cell, given elevations e (NaN = not part of the surface), the
        /// elevation of the outside next to each border cell, and cells that drain at their own elevation.
        /// </summary>
        public static double[,] Flood(double[,] e, Func<int, int, double> outsideLevel, IEnumerable<(int I, int J)> drains)
        {
            int nu = e.GetLength(0), nv = e.GetLength(1);
            var fill = new double[nu, nv];
            var done = new bool[nu, nv];
            var heap = new MinHeap();
            bool Valid(int i, int j) => i >= 0 && j >= 0 && i < nu && j < nv && !double.IsNaN(e[i, j]);
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                    fill[i, j] = double.NaN;

            void Seed(int i, int j, double level)
            {
                var f = Math.Max(e[i, j], level);
                if (double.IsNaN(fill[i, j]) || f < fill[i, j])
                {
                    fill[i, j] = f;
                    heap.Push(f, i, j);
                }
            }

            // Border cells: a neighbour (8-connected) outside the part's footprint is an outlet at the outside level.
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                {
                    if (!Valid(i, j)) continue;
                    var border = false;
                    for (var di = -1; di <= 1 && !border; di++)
                        for (var dj = -1; dj <= 1 && !border; dj++)
                            if ((di != 0 || dj != 0) && !Valid(i + di, j + dj)) border = true;
                    if (border) Seed(i, j, outsideLevel(i, j));
                }
            if (drains != null)
                foreach (var (i, j) in drains)
                    if (Valid(i, j)) Seed(i, j, double.NegativeInfinity);

            while (heap.Count > 0)
            {
                var (level, ci, cj) = heap.Pop();
                if (done[ci, cj] || level > fill[ci, cj]) continue;
                done[ci, cj] = true;
                for (var di = -1; di <= 1; di++)
                    for (var dj = -1; dj <= 1; dj++)
                    {
                        int x = ci + di, y = cj + dj;
                        if ((di == 0 && dj == 0) || !Valid(x, y) || done[x, y]) continue;
                        var f = Math.Max(e[x, y], level);
                        if (double.IsNaN(fill[x, y]) || f < fill[x, y])
                        {
                            fill[x, y] = f;
                            heap.Push(f, x, y);
                        }
                    }
            }

            // Pockets with no outlet at all (a footprint without border cells can't happen, but stay safe): air stays.
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                    if (Valid(i, j) && double.IsNaN(fill[i, j])) fill[i, j] = double.PositiveInfinity;
            return fill;
        }

        /// <summary>The air pockets of a ceiling map, largest volume first.</summary>
        public static List<Trap> Find(Input input)
        {
            var c = input.Ceiling;
            int nu = c.GetLength(0), nv = c.GetLength(1);
            double V(int j) => input.VOfColumn == null ? 0 : input.VOfColumn[j];
            var e = new double[nu, nv];
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                    e[i, j] = double.IsNaN(c[i, j]) ? double.NaN : -Tilted(c[i, j], V(j), input.TiltRad);
            var fill = Flood(e, (i, j) => -Tilted(input.PlLevel, V(j), input.TiltRad), input.Vents);

            var trapped = new bool[nu, nv];
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                    trapped[i, j] = !double.IsNaN(e[i, j]) && fill[i, j] - e[i, j] > input.MinDepth;

            var traps = new List<Trap>();
            var cellArea = input.Cell * input.Cell;
            foreach (var comp in Raster.Components(trapped))
            {
                var t = new Trap { Cells = comp };
                var best = double.PositiveInfinity;
                foreach (var (i, j) in comp)
                {
                    var depth = double.IsPositiveInfinity(fill[i, j]) ? 0 : fill[i, j] - e[i, j];
                    t.Volume += depth * cellArea;
                    if (e[i, j] < best)
                    {
                        best = e[i, j];
                        t.HighI = i;
                        t.HighJ = j;
                        t.HighC = c[i, j];
                        t.Depth = depth;
                    }
                }
                t.Area = comp.Count * cellArea;
                traps.Add(t);
            }
            traps = traps.OrderByDescending(t => t.Volume).ToList();
            for (var k = 0; k < traps.Count; k++) traps[k].Id = k + 1;
            return traps;
        }

        /// <summary>A small binary min-heap of (level, i, j).</summary>
        private sealed class MinHeap
        {
            private readonly List<(double L, int I, int J)> _a = new List<(double, int, int)>();
            public int Count => _a.Count;

            public void Push(double level, int i, int j)
            {
                _a.Add((level, i, j));
                var k = _a.Count - 1;
                while (k > 0)
                {
                    var p = (k - 1) / 2;
                    if (_a[p].L <= _a[k].L) break;
                    (_a[p], _a[k]) = (_a[k], _a[p]);
                    k = p;
                }
            }

            public (double, int, int) Pop()
            {
                var top = _a[0];
                var last = _a[_a.Count - 1];
                _a.RemoveAt(_a.Count - 1);
                if (_a.Count > 0)
                {
                    _a[0] = last;
                    var k = 0;
                    while (true)
                    {
                        int l = 2 * k + 1, r = l + 1, m = k;
                        if (l < _a.Count && _a[l].L < _a[m].L) m = l;
                        if (r < _a.Count && _a[r].L < _a[m].L) m = r;
                        if (m == k) break;
                        (_a[m], _a[k]) = (_a[k], _a[m]);
                        k = m;
                    }
                }
                return top;
            }
        }
    }
}
