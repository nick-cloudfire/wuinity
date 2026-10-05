using System;
using System.Collections.Generic;

namespace PREACT.Visualization.MapLayers
{
    /// <summary>A layer turned into pixels: RGBA32, row 0 at the south, ready for a texture.</summary>
    public sealed class LayerImage
    {
        public int Width, Height;

        /// <summary>Width x Height x 4 bytes, lower-left origin (Unity's LoadRawTextureData order for RGBA32).</summary>
        public byte[] Rgba;

        /// <summary>The smallest and largest valid stored value (before the layer's scale); 0, 0 when none is valid.</summary>
        public double Min, Max;

        public int ValidCells;

        /// <summary>Valid cells above zero: a mask's cells in it.</summary>
        public int PositiveCells;

        /// <summary>For a categorical layer: each code present and how many cells hold it, ascending.</summary>
        public SortedDictionary<int, int> Classes = new SortedDictionary<int, int>();
    }

    /// <summary>
    /// The colours of the input layers: fuel models in LANDFIRE's symbology, quantities on a sequential ramp, directions
    /// on a cyclic one, masks in one colour. Pure arithmetic, so it runs on a worker and is tested head-less.
    /// </summary>
    public static class LayerColoring
    {
        //Viridis at nine evenly spaced stops (matplotlib's viridis at 0, 1/8 ... 1, to within a unit or two per channel):
        //perceptually uniform and readable by the colour-blind, and dark-to-light so a satellite image underneath
        //still shows where it is lighter.
        private static readonly byte[,] Viridis =
        {
            { 68, 1, 84 }, { 71, 44, 122 }, { 59, 81, 139 }, { 44, 113, 142 }, { 33, 144, 141 },
            { 39, 173, 129 }, { 92, 200, 99 }, { 170, 220, 50 }, { 253, 231, 37 },
        };

        /// <summary>The mask colour (cyan, as the result overlay's masks are).</summary>
        public static readonly byte[] MaskColor = { 26, 217, 255 };

        /// <summary>Whether a stored value is a value at all: not NaN, not infinite, not the nodata value, not -9999 or below.</summary>
        public static bool IsValid(double v, double noData)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= -9999.0) return false;
            return !(System.Math.Abs(v - noData) <= 1e-6 * System.Math.Max(1.0, System.Math.Abs(noData)));
        }

        /// <summary>A sequential ramp at <paramref name="t"/> in 0..1.</summary>
        public static void Sequential(double t, out byte r, out byte g, out byte b)
        {
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            double x = t * (Viridis.GetLength(0) - 1);
            int i = System.Math.Min((int)x, Viridis.GetLength(0) - 2);
            double f = x - i;
            r = (byte)System.Math.Round(Viridis[i, 0] + f * (Viridis[i + 1, 0] - Viridis[i, 0]));
            g = (byte)System.Math.Round(Viridis[i, 1] + f * (Viridis[i + 1, 1] - Viridis[i, 1]));
            b = (byte)System.Math.Round(Viridis[i, 2] + f * (Viridis[i + 1, 2] - Viridis[i, 2]));
        }

        /// <summary>
        /// A cyclic ramp for a direction in degrees: a hue wheel with north red, east yellow-green, south cyan-blue and
        /// west violet, so 359 and 1 look alike, as they are.
        /// </summary>
        public static void Cyclic(double degrees, out byte r, out byte g, out byte b)
        {
            double h = degrees % 360.0;
            if (h < 0) h += 360.0;
            Hsv(h, 0.65, 0.95, out r, out g, out b);
        }

        /// <summary>A distinct colour per code, for codes with no standard table.</summary>
        public static void Class(int code, out byte r, out byte g, out byte b)
        {
            double h = (code * 137.50776405) % 360.0;
            if (h < 0) h += 360.0;
            Hsv(h, 0.6, 0.9, out r, out g, out b);
        }

        private static void Hsv(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            double c = v * s;
            double x = c * (1.0 - System.Math.Abs((h / 60.0) % 2.0 - 1.0));
            double m = v - c;
            double rr, gg, bb;
            if (h < 60) { rr = c; gg = x; bb = 0; }
            else if (h < 120) { rr = x; gg = c; bb = 0; }
            else if (h < 180) { rr = 0; gg = c; bb = x; }
            else if (h < 240) { rr = 0; gg = x; bb = c; }
            else if (h < 300) { rr = x; gg = 0; bb = c; }
            else { rr = c; gg = 0; bb = x; }
            r = (byte)System.Math.Round((rr + m) * 255.0);
            g = (byte)System.Math.Round((gg + m) * 255.0);
            b = (byte)System.Math.Round((bb + m) * 255.0);
        }

        /// <summary>
        /// Colours <paramref name="data"/> ([x, y], y = 0 at the south, as <see cref="PREACT.Utility.AscRaster"/> reads) by
        /// <paramref name="style"/>, at <paramref name="opacity"/> (0..1). Invalid cells, fuel code 0 and cells outside a
        /// mask are transparent. A sequential ramp spans the valid values' range, so it is relative to this raster.
        /// </summary>
        public static LayerImage Colorize(float[,] data, double noData, LayerStyle style, float opacity)
        {
            int w = data.GetLength(0), h = data.GetLength(1);
            var image = new LayerImage { Width = w, Height = h, Rgba = new byte[w * h * 4] };
            byte alpha = (byte)System.Math.Round(System.Math.Max(0f, System.Math.Min(1f, opacity)) * 255f);
            bool categorical = style == LayerStyle.FuelModel40 || style == LayerStyle.FuelModel13 || style == LayerStyle.Classes;

            double min = double.MaxValue, max = double.MinValue;
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    double v = data[x, y];
                    if (!IsValid(v, noData)) continue;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    ++image.ValidCells;
                    if (v > 0.0) ++image.PositiveCells;
                    if (categorical)
                    {
                        int code = (int)System.Math.Round(v);
                        image.Classes.TryGetValue(code, out int n);
                        image.Classes[code] = n + 1;
                    }
                }
            }
            if (image.ValidCells == 0) { min = max = 0.0; }
            image.Min = min;
            image.Max = max;
            double range = max - min;
            FuelModelPalette.FuelSystem system = style == LayerStyle.FuelModel13
                ? FuelModelPalette.FuelSystem.Fbfm13
                : FuelModelPalette.FuelSystem.Fbfm40;

            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    double v = data[x, y];
                    int o = (x + y * w) * 4;
                    if (!IsValid(v, noData)) continue;
                    byte r, g, b;
                    switch (style)
                    {
                        case LayerStyle.FuelModel40:
                        case LayerStyle.FuelModel13:
                            int code = (int)System.Math.Round(v);
                            if (code <= 0) continue;
                            FuelModelPalette.Color(system, code, out r, out g, out b);
                            break;
                        case LayerStyle.Classes:
                            int cls = (int)System.Math.Round(v);
                            if (cls <= 0) continue;
                            Class(cls, out r, out g, out b);
                            break;
                        case LayerStyle.Mask:
                            if (v <= 0.0) continue;
                            r = MaskColor[0]; g = MaskColor[1]; b = MaskColor[2];
                            break;
                        case LayerStyle.Cyclic:
                            //ELMFIRE's flat cells and wind with no direction are negative; nothing to point.
                            if (v < 0.0) continue;
                            Cyclic(v, out r, out g, out b);
                            break;
                        default:
                            Sequential(range > 0.0 ? (v - min) / range : 0.0, out r, out g, out b);
                            break;
                    }
                    image.Rgba[o] = r;
                    image.Rgba[o + 1] = g;
                    image.Rgba[o + 2] = b;
                    image.Rgba[o + 3] = alpha;
                }
            }
            return image;
        }
    }
}
