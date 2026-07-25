namespace kperil
{
    class Helpers
    {
        private static float[][] ToJagged(float[,] source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            int rows = source.GetLength(0);
            int cols = source.GetLength(1);
            var result = new float[rows][];

            for (int r = 0; r < rows; r++)
            {
                var row = new float[cols];
                for (int c = 0; c < cols; c++)
                {
                    row[c] = source[r, c];
                }
                result[r] = row;
            }

            return result;
        }

        public static float[,] Copy2D_Fast(float[,] source)
        {
            int rows = source.GetLength(0);
            int cols = source.GetLength(1);
            float[,] dest = new float[rows, cols];

            int bytes = sizeof(float) * rows * cols;
            Buffer.BlockCopy(source, 0, dest, 0, bytes);

            return dest;
        }


        public static float[][] CopyJagged(float[][] src)
        {
            var copy = new float[src.Length][];
            for (int i = 0; i < src.Length; i++)
            {
                copy[i] = (float[])src[i].Clone();
            }
            return copy;
        }
                public static void PrintRaster(float[][] raster)
        {
            if (raster == null || raster.Length == 0)
            {
                Console.WriteLine("Raster is empty or null.");
                return;
            }

            int nrows = raster.Length;
            int ncols = raster[0].Length;

            Console.WriteLine($"Raster size: {nrows} x {ncols}\n");

            for (int i = 0; i < nrows; i++)
            {
                for (int j = 0; j < ncols; j++)
                {
                    Console.Write($"{raster[i][j],8:F2} ");
                }
                Console.WriteLine();
            }
        }
    }
}