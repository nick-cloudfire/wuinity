//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Evacuation
{
    /// <summary>
    /// The ground an evacuation group covers - the cells of its painted mask, or the polygons of its shapefile - in
    /// simulation coordinates (metres from the simulation's UTM origin), and, put together, the WUI area: what k-PERIL
    /// protects, what the case build writes as <c>wui_area.tif</c> and what a trigger campaign aims its wind at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One definition for all of them. The WUI area used to be a second, separately painted mask (Fire areas), which
    /// said nothing about who evacuates; the trigger boundary tab already offered the groups instead, so the two could
    /// disagree on what was being protected. A household's group (<see cref="EvacuationGroup"/>) and the WUI area are
    /// now answered by this one class, without a running simulation, so the case build and the campaign set-up read
    /// the same cells a run does.
    /// </para>
    /// <para>
    /// A mask is read in simulation coordinates, which is how the painter writes it (its header's corner is the paint
    /// grid's corner minus the simulation origin). A shapefile's points are taken as longitude and latitude, or
    /// transformed to them when the layer declares a projected CRS. Polygons keep their rings apart and are tested by
    /// the even-odd rule over all of them, so a hole is a hole and two parts of a multipolygon are two parts - joining
    /// every ring into one point list, as the group test did, drew edges between them.
    /// </para>
    /// </remarks>
    public sealed class EvacuationGroupArea
    {
        public string Name { get; private set; }

        /// <summary>The file the area was read from, as the scenario names it.</summary>
        public string Source { get; private set; }

        //A mask: cell (x, y), y from the south, at a lower-left corner in simulation coordinates.
        private bool[] _mask;
        private int _maskCols, _maskRows;
        private double _maskX, _maskY, _maskCell;

        //Polygons: each ring its own closed list of points, in simulation coordinates.
        private readonly List<Vector2d[]> _rings = new List<Vector2d[]>();

        private Vector2d _min = new Vector2d(double.MaxValue, double.MaxValue);
        private Vector2d _max = new Vector2d(double.MinValue, double.MinValue);

        /// <summary>True when the area covers nothing at all: no mask cell set and no polygon.</summary>
        public bool IsEmpty { get => _min.x > _max.x; }

        /// <summary>
        /// Whether the area was given by a file that could be read. A group with neither a mask nor a shapefile, or one
        /// whose file failed, has no area, and nobody can belong to it.
        /// </summary>
        public bool HasArea { get => _mask != null || _rings.Count > 0; }

        private EvacuationGroupArea() { }

        /// <summary>
        /// Reads a group's area from its <c>MaskFile</c> (which wins when both are named, as everywhere) or its
        /// <c>ShapeFile</c>. Never throws: what went wrong is in <paramref name="problem"/>, with <paramref name="fatal"/>
        /// true when a named file could not be read (as against a group that names none, or a mask that marks nothing).
        /// </summary>
        public static EvacuationGroupArea Load(EvacuationGroupInput group, string rootFolder, SimulationData simulation,
            out string problem, out bool fatal)
        {
            problem = null;
            fatal = false;
            var area = new EvacuationGroupArea { Name = group?.Name ?? string.Empty };
            if (group == null) return area;

            try
            {
                if (!string.IsNullOrEmpty(group.MaskFile))
                {
                    area.Source = group.MaskFile;
                    area.LoadMask(PREACTInput.ResolvePath(rootFolder, group.MaskFile), out problem, out fatal);
                }
                else if (!string.IsNullOrEmpty(group.ShapeFile))
                {
                    area.Source = group.ShapeFile;
                    area.LoadShapeFile(PREACTInput.ResolvePath(rootFolder, group.ShapeFile), simulation, out problem, out fatal);
                }
            }
            catch (Exception e)
            {
                problem = $"Evacuation group {area.Name}: could not read its area {area.Source} ({e.Message}).";
                fatal = true;
                area._mask = null;
                area._rings.Clear();
            }

            if (problem == null && area.HasArea && area.IsEmpty)
            {
                problem = $"Evacuation group {area.Name}: its area {area.Source} marks no cells, so nobody belongs to it.";
            }
            return area;
        }

        private void LoadMask(string path, out string problem, out bool fatal)
        {
            problem = null;
            fatal = false;
            float[,] raster = Utility.AscRaster.Read(path, out Utility.AscRaster.Header header, out bool ok);
            if (!ok || raster == null)
            {
                problem = $"Evacuation group {Name}: could not read its area mask {path}.";
                fatal = true;
                return;
            }

            if (header.CellSize <= 0.0)
            {
                problem = $"Evacuation group {Name}: its area mask has a cell size of {header.CellSize}.";
                fatal = true;
                return;
            }

            _maskCols = header.Ncols;
            _maskRows = header.Nrows;
            _maskX = header.XllCorner;
            _maskY = header.YllCorner;
            _maskCell = header.CellSize;
            _mask = new bool[_maskCols * _maskRows];

            for (int y = 0; y < _maskRows; ++y)
            {
                for (int x = 0; x < _maskCols; ++x)
                {
                    float v = raster[x, y];
                    if (!(v > 0f) || v == (float)header.NoDataValue) continue;

                    _mask[x + y * _maskCols] = true;
                    Grow(_maskX + x * _maskCell, _maskY + y * _maskCell);
                    Grow(_maskX + (x + 1) * _maskCell, _maskY + (y + 1) * _maskCell);
                }
            }
        }

        private void LoadShapeFile(string path, SimulationData simulation, out string problem, out bool fatal)
        {
            problem = null;
            fatal = false;

            OSGeo.OGR.Ogr.RegisterAll();
            using (OSGeo.OGR.DataSource dataSource = OSGeo.OGR.Ogr.Open(path, 0))
            {
                if (dataSource == null)
                {
                    problem = $"Evacuation group {Name}: could not open its shapefile {path}.";
                    fatal = true;
                    return;
                }

                for (int i = 0; i < dataSource.GetLayerCount(); ++i)
                {
                    OSGeo.OGR.Layer layer = dataSource.GetLayerByIndex(i);
                    OSGeo.OSR.CoordinateTransformation toWgs84 = ToWgs84(layer.GetSpatialRef());
                    try
                    {
                        layer.ResetReading();
                        OSGeo.OGR.Feature feature;
                        while ((feature = layer.GetNextFeature()) != null)
                        {
                            using (feature)
                            {
                                OSGeo.OGR.Geometry geometry = feature.GetGeometryRef();
                                if (geometry != null) AddRings(geometry, toWgs84, simulation);
                            }
                        }
                    }
                    finally
                    {
                        toWgs84?.Dispose();
                    }
                }
            }

            if (_rings.Count == 0)
            {
                problem = $"Evacuation group {Name}: its shapefile {path} holds no polygon, so nobody belongs to it.";
            }
        }

        /// <summary>To longitude/latitude from a layer's own CRS when it declares a projected one; null for none or WGS84.</summary>
        private static OSGeo.OSR.CoordinateTransformation ToWgs84(OSGeo.OSR.SpatialReference layerSrs)
        {
            if (layerSrs == null || layerSrs.IsGeographic() == 1) return null;

            var wgs84 = new OSGeo.OSR.SpatialReference(string.Empty);
            wgs84.ImportFromEPSG(4326);
            //Easting/longitude first on both sides, whatever the CRS's own axis order: the points are read that way.
            wgs84.SetAxisMappingStrategy(OSGeo.OSR.AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            layerSrs.SetAxisMappingStrategy(OSGeo.OSR.AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            return new OSGeo.OSR.CoordinateTransformation(layerSrs, wgs84);
        }

        private void AddRings(OSGeo.OGR.Geometry geometry, OSGeo.OSR.CoordinateTransformation toWgs84, SimulationData simulation)
        {
            OSGeo.OGR.wkbGeometryType type = OSGeo.OGR.Ogr.GT_Flatten(geometry.GetGeometryType());
            if (type == OSGeo.OGR.wkbGeometryType.wkbLineString || type == OSGeo.OGR.wkbGeometryType.wkbLinearRing)
            {
                int n = geometry.GetPointCount();
                if (n < 3) return;

                var ring = new Vector2d[n];
                for (int i = 0; i < n; ++i)
                {
                    double[] p = { geometry.GetX(i), geometry.GetY(i), 0.0 };
                    if (toWgs84 != null) toWgs84.TransformPoint(p);
                    //Longitude, latitude in the file; the simulation measures from latitude, longitude.
                    Vector2d local = simulation.GetSimulationPosition(new Vector2d(p[1], p[0]));
                    ring[i] = local;
                    Grow(local.x, local.y);
                }
                _rings.Add(ring);
                return;
            }

            //Polygons (their rings), multipolygons and collections (their parts).
            for (int i = 0; i < geometry.GetGeometryCount(); ++i)
            {
                AddRings(geometry.GetGeometryRef(i), toWgs84, simulation);
            }
        }

        private void Grow(double x, double y)
        {
            if (x < _min.x) _min.x = x;
            if (y < _min.y) _min.y = y;
            if (x > _max.x) _max.x = x;
            if (y > _max.y) _max.y = y;
        }

        /// <summary>Whether a point in simulation coordinates is in the area.</summary>
        public bool Contains(Vector2d p)
        {
            if (IsEmpty || p.x < _min.x || p.x > _max.x || p.y < _min.y || p.y > _max.y) return false;

            if (_mask != null)
            {
                int x = (int)System.Math.Floor((p.x - _maskX) / _maskCell);
                int y = (int)System.Math.Floor((p.y - _maskY) / _maskCell);
                return x >= 0 && y >= 0 && x < _maskCols && y < _maskRows && _mask[x + y * _maskCols];
            }

            //Even-odd over every ring: inside an odd number of them.
            bool inside = false;
            foreach (Vector2d[] ring in _rings)
            {
                Vector2d a = ring[ring.Length - 1];
                foreach (Vector2d b in ring)
                {
                    if ((b.y > p.y) != (a.y > p.y) && p.x < (a.x - b.x) * (p.y - b.y) / (a.y - b.y) + b.x)
                    {
                        inside = !inside;
                    }
                    a = b;
                }
            }
            return inside;
        }

        // ------------------------------------------------------------------ the WUI area of several groups

        /// <summary>
        /// Reads every group of the scenario that names an area. What could not be read, or marks nothing, is reported
        /// through <paramref name="log"/> and left out.
        /// </summary>
        public static List<EvacuationGroupArea> LoadAll(IEnumerable<EvacuationGroupInput> groups, string rootFolder,
            SimulationData simulation, Action<string> log)
        {
            var areas = new List<EvacuationGroupArea>();
            if (groups == null) return areas;

            foreach (EvacuationGroupInput group in groups)
            {
                if (string.IsNullOrEmpty(group.MaskFile) && string.IsNullOrEmpty(group.ShapeFile)) continue;

                EvacuationGroupArea area = Load(group, rootFolder, simulation, out string problem, out bool _);
                if (problem != null) log?.Invoke(problem);
                if (area.HasArea && !area.IsEmpty) areas.Add(area);
            }
            return areas;
        }

        /// <summary>
        /// The union of <paramref name="areas"/> on a georeferenced grid: one flag per cell, indexed x + y * ncols with y
        /// from the south (how every mask here is held), set where the cell's centre is in any of them.
        /// </summary>
        /// <remarks>
        /// The cell centres are measured in the simulation's UTM zone first - the same zone the masks and polygons are
        /// in - transformed through GDAL when the grid is in another (a case cut in its centre's zone for a domain whose
        /// corner is across a zone edge).
        /// </remarks>
        public static bool[] Rasterize(IList<EvacuationGroupArea> areas, Utility.MasterGrid grid, SimulationData simulation,
            out int cells)
        {
            int ncols = grid.Header.Ncols, nrows = grid.Header.Nrows;
            double cs = grid.Header.CellSize;
            var union = new bool[ncols * nrows];
            cells = 0;
            if (areas == null || areas.Count == 0) return union;

            double[] xs = new double[ncols * nrows];
            double[] ys = new double[ncols * nrows];
            for (int y = 0; y < nrows; ++y)
            {
                for (int x = 0; x < ncols; ++x)
                {
                    xs[x + y * ncols] = grid.XMin + (x + 0.5) * cs;
                    ys[x + y * ncols] = grid.YMin + (y + 0.5) * cs;
                }
            }

            string simulationEpsg = "EPSG:" + simulation.UtmEpsgCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(grid.Epsg) && !string.Equals(grid.Epsg, simulationEpsg, StringComparison.OrdinalIgnoreCase))
            {
                Transform(grid.Epsg, simulationEpsg, xs, ys);
            }

            Vector2d origin = simulation.UTMOrigin;
            for (int i = 0; i < xs.Length; ++i)
            {
                var p = new Vector2d(xs[i] - origin.x, ys[i] - origin.y);
                foreach (EvacuationGroupArea area in areas)
                {
                    if (!area.Contains(p)) continue;
                    union[i] = true;
                    ++cells;
                    break;
                }
            }
            return union;
        }

        private static void Transform(string fromEpsg, string toEpsg, double[] xs, double[] ys)
        {
            using (var from = new OSGeo.OSR.SpatialReference(string.Empty))
            using (var to = new OSGeo.OSR.SpatialReference(string.Empty))
            {
                from.SetFromUserInput(fromEpsg);
                to.SetFromUserInput(toEpsg);
                from.SetAxisMappingStrategy(OSGeo.OSR.AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                to.SetAxisMappingStrategy(OSGeo.OSR.AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                using (var transform = new OSGeo.OSR.CoordinateTransformation(from, to))
                {
                    transform.TransformPoints(xs.Length, xs, ys, new double[xs.Length]);
                }
            }
        }

        /// <summary>What comparing a case's <c>wui_area.tif</c> with the evacuation groups found.</summary>
        public sealed class WuiAreaComparison
        {
            public enum Failure { None, NoGroupArea, NoCaseGrid, NotOnCaseGrid }

            /// <summary>Why the two could not be compared, or <see cref="Failure.None"/>.</summary>
            public Failure Problem;
            public string ProblemDetail = string.Empty;

            /// <summary>Cells marked in the file, in the groups' union, and that differ between the two.</summary>
            public int FileCells, GroupCells, Differ;

            /// <summary>The names of the groups that have an area, comma-separated.</summary>
            public string Groups = string.Empty;

            public bool Same => Problem == Failure.None && Differ == 0;
        }

        /// <summary>
        /// Compares a case's WUI area, <paramref name="wuiAreaPath"/>, cell by cell with the union of the
        /// <paramref name="groups"/>' areas on the case grid (<c>dem.tif</c> in <paramref name="inputsDirectory"/>) - the test
        /// a campaign refuses a stale <c>wui_area.tif</c> with, and the GUI's workflow warns by. What could not be read of
        /// a group is reported through <paramref name="log"/>.
        /// </summary>
        public static WuiAreaComparison CompareWithCase(IEnumerable<EvacuationGroupInput> groups, string rootFolder,
            SimulationData simulation, string inputsDirectory, string wuiAreaPath, Action<string> log)
        {
            var result = new WuiAreaComparison();
            List<EvacuationGroupArea> areas = LoadAll(groups, rootFolder, simulation, log);
            result.Groups = Names(areas);
            if (areas.Count == 0)
            {
                result.Problem = WuiAreaComparison.Failure.NoGroupArea;
                return result;
            }

            Utility.MasterGrid grid;
            try
            {
                grid = Utility.MasterGrid.FromRasterFile(Path.Combine(inputsDirectory, Utility.ElmfireStems.Dem + ".tif"));
            }
            catch (Exception e)
            {
                result.Problem = WuiAreaComparison.Failure.NoCaseGrid;
                result.ProblemDetail = e.Message;
                return result;
            }

            bool[] union = Rasterize(areas, grid, simulation, out result.GroupCells);
            float[,] mask = Utility.AscRaster.ReadGeoTiff(wuiAreaPath, out Utility.AscRaster.Header header, out bool ok);
            if (!ok || mask == null || header.Ncols != grid.Header.Ncols || header.Nrows != grid.Header.Nrows)
            {
                result.Problem = WuiAreaComparison.Failure.NotOnCaseGrid;
                return result;
            }

            for (int y = 0; y < header.Nrows; ++y)
            {
                for (int x = 0; x < header.Ncols; ++x)
                {
                    bool marked = mask[x, y] > 0f && mask[x, y] != (float)header.NoDataValue;
                    if (marked) ++result.FileCells;
                    if (marked != union[x + y * header.Ncols]) ++result.Differ;
                }
            }
            return result;
        }

        /// <summary>The group names of <paramref name="areas"/>, comma-separated, in order.</summary>
        public static string Names(IEnumerable<EvacuationGroupArea> areas)
        {
            var names = new List<string>();
            if (areas != null) foreach (EvacuationGroupArea a in areas) names.Add(a.Name);
            return string.Join(",", names);
        }
    }
}
