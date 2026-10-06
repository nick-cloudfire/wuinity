//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PREACT.Utility
{
    /// <summary>
    /// How a fuel map's fire-carrying cells hang together: its 8-connected patches, how many are a single cell, the
    /// largest one's share, and - the question roads raise - how much of the WUI area the patch an ignition sits in
    /// reaches.
    /// </summary>
    /// <remarks>
    /// 8-connected, because a fire crosses a diagonal. Iterative, because a patch covering most of a million-cell raster
    /// overflows a recursive fill's stack. A WUI cell counts as reached when it, or one of its eight neighbours, is in a
    /// patch an ignition is in: WUI cells are mostly urban (non-burnable) themselves, and the fire arriving at their edge
    /// is what matters.
    /// </remarks>
    public static class FuelConnectivity
    {
        public sealed class Stats
        {
            public int Patches, Islets, LargestPatch;
            public long FireCarrying;

            /// <summary>WUI cells, and those reached from an ignition's patch; -1 when there was no WUI area or no ignition.</summary>
            public long WuiCells = -1, WuiReached = -1;

            /// <summary>The share of fire-carrying cells in the largest patch, as text.</summary>
            public string LargestShare => FireCarrying <= 0
                ? "n/a"
                : (100.0 * LargestPatch / FireCarrying).ToString("0.#", CultureInfo.InvariantCulture) + " %";

            public string WuiShare => WuiCells <= 0 || WuiReached < 0
                ? "n/a"
                : (100.0 * WuiReached / WuiCells).ToString("0.#", CultureInfo.InvariantCulture) + " %";
        }

        /// <summary>
        /// Labels the 8-connected patches of cells <paramref name="carries"/> says carry fire: 0 for a cell that does not,
        /// 1.. for the patches. <paramref name="sizes"/>[label] is a patch's cell count (index 0 unused).
        /// </summary>
        public static int[,] Label(float[,] fuel, Func<float, bool> carries, out List<int> sizes)
        {
            int nx = fuel.GetLength(0), ny = fuel.GetLength(1);
            var labels = new int[nx, ny];
            sizes = new List<int> { 0 };
            var stack = new Stack<int>();

            for (int sx = 0; sx < nx; ++sx)
            {
                for (int sy = 0; sy < ny; ++sy)
                {
                    if (labels[sx, sy] != 0 || !carries(fuel[sx, sy])) continue;

                    int label = sizes.Count;
                    int size = 0;
                    labels[sx, sy] = label;
                    stack.Push(sx * ny + sy);
                    while (stack.Count > 0)
                    {
                        int packed = stack.Pop();
                        int x = packed / ny, y = packed % ny;
                        ++size;
                        for (int dx = -1; dx <= 1; ++dx)
                        {
                            for (int dy = -1; dy <= 1; ++dy)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int ax = x + dx, ay = y + dy;
                                if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                                if (labels[ax, ay] != 0 || !carries(fuel[ax, ay])) continue;
                                labels[ax, ay] = label;
                                stack.Push(ax * ny + ay);
                            }
                        }
                    }
                    sizes.Add(size);
                }
            }
            return labels;
        }

        /// <summary>
        /// The statistics of <paramref name="fuel"/>, and the WUI reach when a WUI mask (on the same grid, &gt; 0 inside)
        /// and ignition cells (x east, y north, as <see cref="AscRaster"/> reads) are given.
        /// </summary>
        public static Stats Measure(float[,] fuel, Func<float, bool> carries, float[,] wui = null, IList<(int X, int Y)> ignitions = null)
        {
            var stats = new Stats();
            int[,] labels = Label(fuel, carries, out List<int> sizes);
            stats.Patches = sizes.Count - 1;
            for (int i = 1; i < sizes.Count; ++i)
            {
                stats.FireCarrying += sizes[i];
                if (sizes[i] == 1) ++stats.Islets;
                if (sizes[i] > stats.LargestPatch) stats.LargestPatch = sizes[i];
            }

            int nx = fuel.GetLength(0), ny = fuel.GetLength(1);
            if (wui == null || ignitions == null || ignitions.Count == 0 || wui.GetLength(0) != nx || wui.GetLength(1) != ny)
            {
                return stats;
            }

            //The patches the ignitions are in - or touch, for a point on a non-burnable cell beside fuel.
            var lit = new HashSet<int>();
            foreach ((int X, int Y) p in ignitions)
            {
                if (p.X < 0 || p.Y < 0 || p.X >= nx || p.Y >= ny) continue;
                if (labels[p.X, p.Y] != 0)
                {
                    lit.Add(labels[p.X, p.Y]);
                    continue;
                }
                for (int dx = -1; dx <= 1; ++dx)
                {
                    for (int dy = -1; dy <= 1; ++dy)
                    {
                        int x = p.X + dx, y = p.Y + dy;
                        if (x < 0 || y < 0 || x >= nx || y >= ny || labels[x, y] == 0) continue;
                        lit.Add(labels[x, y]);
                    }
                }
            }

            stats.WuiCells = 0;
            stats.WuiReached = 0;
            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    if (!(wui[x, y] > 0f)) continue;
                    ++stats.WuiCells;
                    bool reached = false;
                    for (int dx = -1; dx <= 1 && !reached; ++dx)
                    {
                        for (int dy = -1; dy <= 1 && !reached; ++dy)
                        {
                            int ax = x + dx, ay = y + dy;
                            if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                            if (labels[ax, ay] != 0 && lit.Contains(labels[ax, ay])) reached = true;
                        }
                    }
                    if (reached) ++stats.WuiReached;
                }
            }
            return stats;
        }

        /// <summary>One line comparing two measurements, for a log.</summary>
        public static string Compare(Stats before, Stats after)
        {
            string line = $"burnable patches {before.Patches} -> {after.Patches}; single-cell islets {before.Islets} -> {after.Islets}; "
                          + $"largest patch {before.LargestShare} -> {after.LargestShare} of the cells that carry fire";
            if (before.WuiCells > 0 && after.WuiCells > 0)
            {
                line += $"; WUI area reached by the ignition's fuel patch {before.WuiReached} -> {after.WuiReached} of {after.WuiCells} cells "
                        + $"({before.WuiShare} -> {after.WuiShare})";
            }
            return line;
        }
    }
}
