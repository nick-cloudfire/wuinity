using System;
using System.Globalization;
using PREACT.Utility;

namespace PREACT.Visualization.MapLayers
{
    /// <summary>
    /// Which cell of a raster a map point falls in, and how a layer's value reads in words: the arithmetic behind the
    /// map's point info and the layer legends, with no Unity in it.
    /// </summary>
    public static class LayerValues
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// The cell of the grid <paramref name="h"/> that contains (<paramref name="x"/>, <paramref name="y"/>) in the
        /// grid's own coordinates: <paramref name="col"/> from the west edge and <paramref name="row"/> from the north edge,
        /// both from 0 - GDAL's pixel and line, as QGIS shows them. False outside the grid.
        /// </summary>
        public static bool TryCell(AscRaster.Header h, double x, double y, out int col, out int row)
        {
            col = row = -1;
            double cx = h.CellSize, cy = h.CellSizeY > 0.0 ? h.CellSizeY : h.CellSize;
            if (h.Ncols <= 0 || h.Nrows <= 0 || cx <= 0.0) return false;
            double fx = (x - h.XllCorner) / cx;
            double fy = (y - h.YllCorner) / cy;
            if (fx < 0.0 || fy < 0.0 || fx >= h.Ncols || fy >= h.Nrows) return false;
            col = (int)System.Math.Floor(fx);
            row = h.Nrows - 1 - (int)System.Math.Floor(fy);
            return true;
        }

        /// <summary>
        /// The texture size for a raster of <paramref name="ncols"/> x <paramref name="nrows"/>: itself when neither side
        /// exceeds <paramref name="maxSide"/>, otherwise scaled down evenly so the longer side is <paramref name="maxSide"/>.
        /// </summary>
        public static void DisplaySize(int ncols, int nrows, int maxSide, out int width, out int height)
        {
            int longer = System.Math.Max(ncols, nrows);
            if (maxSide <= 0 || longer <= maxSide)
            {
                width = ncols;
                height = nrows;
                return;
            }
            double f = (double)maxSide / longer;
            width = System.Math.Max(1, (int)System.Math.Round(ncols * f));
            height = System.Math.Max(1, (int)System.Math.Round(nrows * f));
        }

        /// <summary>The compass point of a direction in degrees: N, NE, E ... NW.</summary>
        public static string Compass(double degrees)
        {
            string[] points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            double d = degrees % 360.0;
            if (d < 0) d += 360.0;
            return points[(int)System.Math.Floor((d + 22.5) / 45.0) % 8];
        }

        /// <summary>A number in a unit, with as many decimals as its size needs: "845 m", "12.4 m", "0.125 kg/m³", "45%".</summary>
        public static string Number(double value, string unit)
        {
            double a = System.Math.Abs(value);
            string format = a >= 100.0 || (a == System.Math.Round(a) && a >= 10.0) ? "0" : a >= 10.0 ? "0.0" : a >= 1.0 ? "0.##" : "0.###";
            string n = value.ToString(format, Inv);
            if (string.IsNullOrEmpty(unit)) return n;
            return unit == "%" || unit == "°" ? n + unit : n + " " + unit;
        }

        /// <summary>
        /// One stored value of <paramref name="layer"/> in words: "102 GR2 - Low load, dry climate grass", "12.4 m",
        /// "225° (SW)", "270° (from W)", "in the mask", "no data".
        /// </summary>
        public static string Describe(InputLayer layer, double raw, double noData)
        {
            if (!LayerColoring.IsValid(raw, noData)) return "no data";
            switch (layer.Style)
            {
                case LayerStyle.FuelModel40:
                case LayerStyle.FuelModel13:
                    int code = (int)System.Math.Round(raw);
                    if (code <= 0) return "no fuel model (" + code.ToString(Inv) + ")";
                    return FuelModelPalette.Describe(layer.Style == LayerStyle.FuelModel13
                        ? FuelModelPalette.FuelSystem.Fbfm13
                        : FuelModelPalette.FuelSystem.Fbfm40, code);
                case LayerStyle.Classes:
                    return "code " + ((int)System.Math.Round(raw)).ToString(Inv);
                case LayerStyle.Mask:
                    return raw > 0.0 ? "in (" + Number(raw, string.Empty) + ")" : "out (0)";
                case LayerStyle.Cyclic:
                    double d = raw * layer.Scale;
                    if (d < 0.0) return layer.IsWindDirection ? "calm (" + Number(d, string.Empty) + ")" : "flat (" + Number(d, string.Empty) + ")";
                    return Number(d, "°") + (layer.IsWindDirection ? " (from " + Compass(d) + ")" : " (" + Compass(d) + ")");
                default:
                    return Number(raw * layer.Scale, layer.Unit);
            }
        }

        /// <summary>The legend line of a continuous layer, in real units: "12 - 845 m".</summary>
        public static string Range(InputLayer layer, double rawMin, double rawMax)
        {
            double a = rawMin * layer.Scale, b = rawMax * layer.Scale;
            if (b < a) { double t = a; a = b; b = t; }
            return Number(a, string.Empty) + " - " + Number(b, layer.Unit);
        }

        /// <summary>A weather band's time: "band 3 of 72, +2 h" for DT_METEOROLOGY 3600 s.</summary>
        public static string Band(int band, int bands, double dtSeconds)
        {
            double seconds = (band - 1) * (dtSeconds > 0.0 ? dtSeconds : 3600.0);
            string at = seconds % 3600.0 == 0.0
                ? "+" + (seconds / 3600.0).ToString("0", Inv) + " h"
                : "+" + (seconds / 60.0).ToString("0.#", Inv) + " min";
            return "band " + band.ToString(Inv) + " of " + bands.ToString(Inv) + ", " + at;
        }

        /// <summary>A time-of-arrival cell: "2.35 h after ignition (8460 s)", or "not reached".</summary>
        public static string ArrivalTime(double seconds, double noData)
        {
            if (!LayerColoring.IsValid(seconds, noData) || seconds < 0.0) return "not reached";
            return (seconds / 3600.0).ToString("0.00", Inv) + " h after ignition (" + seconds.ToString("0", Inv) + " s)";
        }

        /// <summary>A trigger-boundary cell: inside with its value, or outside.</summary>
        public static string TriggerBoundary(double value, double noData)
        {
            if (!LayerColoring.IsValid(value, noData) || value <= 0.0) return "outside";
            return "inside (" + Number(value, string.Empty) + ")";
        }
    }
}
