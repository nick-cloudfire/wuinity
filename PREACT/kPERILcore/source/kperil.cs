using System.Runtime.CompilerServices;
using kperil;
using OSGeo.GDAL;

namespace kPERIL
{
    public class kPERIL
    {
        public perilData perilData;

        public kPERIL()
        {
            this.perilData = new perilData();
            Gdal.AllRegister();
        }

        public struct DirectionMagnitudes
        {
            public float[] directions;

            public DirectionMagnitudes()
            {
                directions = new float[8];
            }
        }

        public DirectionMagnitudes[,] breakdownRateOfSpread()
        {
            perilData.VerifyAllRastersImported();

            int width = perilData.totalX;
            int height = perilData.totalY;
            float a, b, c, rosX, rosY;
            DirectionMagnitudes[,] brokenDownRateOfSpread = new DirectionMagnitudes[width,height];

            //set the size of the broken down ROS raster and at the same time calculate each value.
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float[] directions = new float[8];
                    if (perilData.rateOfSpreadMagnitudeRaster[x,y] >= 0 && perilData.rateOfSpreadMagnitudeRaster[x,y] != perilData.noDataValue)
                    {
                        double lb = 0.936 * Math.Exp(0.2566 * perilData.effectiveMidflameWindspeed[x,y]) + 0.461 * Math.Exp(-0.1548 * perilData.effectiveMidflameWindspeed[x,y]) - 0.397; //Calculate length to Breadth ratio of Huygens ellipse
                        double hb = (lb + (float)Math.Sqrt(lb * lb - 1)) / (lb - (float)Math.Sqrt(lb * lb - 1));    //calculate head to back ratio

                        a = perilData.rateOfSpreadMagnitudeRaster[x,y] / (2 * (float)lb) * (1 + 1 / (float)hb);
                        b = perilData.rateOfSpreadMagnitudeRaster[x,y] * 0.5f * (1 + 1 / (float)hb);
                        c = perilData.rateOfSpreadMagnitudeRaster[x,y] * 0.5f * (1 - 1 / (float)hb);

                        for (int dir = 0; dir < 8; dir++)
                        {
                            rosX = a * (float)Math.Sin((Math.PI * dir / 4) - perilData.rateOfSpreadDirectionRaster[x,y] * 2 * Math.PI / 360);
                            rosY = c + b * (float)Math.Cos((Math.PI * dir / 4) - perilData.rateOfSpreadDirectionRaster[x,y] * 2 * Math.PI / 360);
                            directions[dir] = (float)Math.Sqrt(Math.Pow(rosX, 2) + Math.Pow(rosY, 2));
                        }
                    }
                    else
                    {
                        for (int dir = 0; dir < 8; dir++)
                        {
                            directions[dir] = 0;
                        }
                    }
                    brokenDownRateOfSpread[x, y] = new DirectionMagnitudes { directions = directions };
                }
            }
            return brokenDownRateOfSpread;
        }

        /// <summary>
        /// Calculate time taken for the fire to travel FROM cell x,y TO its neighbor in direction dir. 
        /// </summary>
        /// <param name="brokenDownRateOfSpread"></param>
        /// <returns></returns>
        public DirectionMagnitudes[,] getTravelTimeFromBrokenDownRos(DirectionMagnitudes[,] brokenDownRateOfSpread)
        {
            int width = perilData.totalX;
            int height = perilData.totalY;

            DirectionMagnitudes[,] travelTimeToNextCell = new DirectionMagnitudes[width, height];

            //set the size of the broken down ROS raster and at the same time calculate each value.
            for (int x = 1; x < width - 1; x++)
            {
                for (int y = 1; y < height - 1; y++)
                {
                    float[] travelTimes = new float[8];
                    if (perilData.rateOfSpreadMagnitudeRaster[x,y] >= 0 && perilData.rateOfSpreadMagnitudeRaster[x,y] != perilData.noDataValue)
                    {
                        for (int dir = 0; dir < 8; dir++)
                        {
                            float additionalEdgeTravel = (dir % 2) == 0 ? 1 : 1.4142f;
                            (int xn, int yn) = getNeighborDims(x, y, dir);
                            travelTimes[dir] = 2 * perilData.cellSize * additionalEdgeTravel / (brokenDownRateOfSpread[x,y].directions[dir] + brokenDownRateOfSpread[xn,yn].directions[dir]);
                        }
                    }
                    else
                    {
                        for (int dir = 0; dir < 8; dir++)
                        {
                            travelTimes[dir] = float.NaN;
                        }
                    }
                    travelTimeToNextCell[x, y] = new DirectionMagnitudes { directions = travelTimes };
                }
                
            }
            travelTimeToNextCell = FillRasterEdgesByCopy(travelTimeToNextCell);
            
            return travelTimeToNextCell;
        }

        private DirectionMagnitudes[,] FillRasterEdgesByCopy(DirectionMagnitudes[,] raster)
        {
            int width  = raster.GetLength(0);
            int height = raster.GetLength(1);
            if (width == 0 || height == 0) return raster;

            void CopyCell(int dx, int dy, int sx, int sy)
            {
                var src = raster[sx, sy];
                var dst = raster[dx, dy];
                if (dst.directions == null) raster[dx, dy] = dst = new DirectionMagnitudes();
                Array.Copy(src.directions, dst.directions, 8);
            }

            for (int x = 1; x < width - 1; x++)
            {
                CopyCell(x, 0,          x, 1);
                CopyCell(x, height - 1, x, height - 2);
            }
            for (int y = 0; y < height; y++)
            {
                CopyCell(0,         y, 1,         y);
                CopyCell(width - 1, y, width - 2, y);
            }
            return raster;
        }

        public float[,] breadthFirstSearchSingleCell(
            int x_start,
            int y_start,
            float aset,
            DirectionMagnitudes[,] travelTimeToNextCell)
        {
            int width = perilData.totalX;
            int height = perilData.totalY;

            if (x_start < 0 || x_start >= width || y_start < 0 || y_start >= height)
            {
                throw new ArgumentOutOfRangeException("Start cell is outside the raster bounds.");
            }

            if (perilData.wuiAreaRaster[x_start,y_start] == 2)
            {
                throw new InvalidOperationException("Start cell is internal to the WUI area.");
            }

            float[,] singleCellBfs = new float[width,height];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    singleCellBfs[x,y] = float.PositiveInfinity;
                }
            }

            var frontier = new PriorityQueue<(int x, int y), float>();
            singleCellBfs[x_start,y_start] = 0f;
            frontier.Enqueue((x_start, y_start), 0f);

            while (frontier.TryDequeue(out var current, out float currentCost))
            {
                if (currentCost > singleCellBfs[current.x,current.y])
                {
                    continue; // already found a cheaper path
                }

                for (int dir = 0; dir < 8; dir++)
                {
                    (int nx, int ny) = getNeighborDims(current.x, current.y, dir);

                    if (nx < 0 || ny < 0 || nx >= perilData.totalX || ny >= perilData.totalY) continue; //point out of range
                    if ((perilData.wuiAreaRaster != null && perilData.wuiAreaRaster[nx,ny] != 0)) continue; // blocked or unreached cell

                    float stepCost = travelTimeToNextCell[nx,ny].directions[(dir + 4) % 8];

                    if (stepCost <= 0f || float.IsNaN(stepCost) || float.IsInfinity(stepCost)) continue; // invalid travel time

                    float newCost = currentCost + stepCost;
                    if (newCost < singleCellBfs[nx,ny])
                    {
                        singleCellBfs[nx,ny] = newCost;
                        if (newCost <= aset) frontier.Enqueue((nx, ny), newCost);
                    }
                }
            }
            
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (singleCellBfs[x,y] == float.PositiveInfinity) singleCellBfs[x,y] = float.NaN;
                }
            }

            return singleCellBfs;
        }


        public float[,] getTriggerBoundary(DirectionMagnitudes[,] travelTimeToNextCell, float rset)
        {
            float[,] triggerBoundary = new float[perilData.totalX,perilData.totalY];
            float[,] wuiArea = wuiAreaAdjusted();

            for (int x = 0; x < perilData.totalX; x++)
            {
                for (int y = 0; y < perilData.totalY; y++)
                {
                    if (wuiArea[x,y] == 1)
                    {
                        var singleCellTriggerBoundary = breadthFirstSearchSingleCell(x, y, rset, travelTimeToNextCell);
                        for (int xb = 0; xb < perilData.totalX; xb++)
                        {
                            for (int yb = 0; yb < perilData.totalY; yb++)
                            {
                                if (singleCellTriggerBoundary[xb,yb] >= 0) triggerBoundary[xb,yb] = 1;
                            }
                        }
                    }
                }
            }

            for (int x = 0; x < perilData.totalX; x++)
            {
                for (int y = 0; y < perilData.totalY; y++)
                {
                    if (wuiArea[x,y] > 0)
                    {
                        triggerBoundary[x,y] = wuiArea[x,y] + 1;
                    }
                }
            }
            return triggerBoundary;
        }
        private float[,] wuiAreaAdjusted()
        {
            float[,] wuiAreaAdjusted = Helpers.Copy2D_Fast(perilData.wuiAreaRaster);

            for (int x = 1; x < perilData.totalX - 1; x++)
            {
                for (int y = 1; y < perilData.totalY - 1; y++)
                {
                    if (perilData.wuiAreaRaster[x + 1,y] == 0) continue;
                    if (perilData.wuiAreaRaster[x - 1,y] == 0) continue;
                    if (perilData.wuiAreaRaster[x,y + 1] == 0) continue;
                    if (perilData.wuiAreaRaster[x,y - 1] == 0) continue;
                    wuiAreaAdjusted[x,y] = 2;
                }
            }
            return wuiAreaAdjusted;
        }
        private (int xn, int yn) getNeighborDims(int x, int y, int dir)
        {
            switch (dir)
            {
                case 0: return (x - 1, y);
                case 1: return (x - 1, y + 1);
                case 2: return (x, y + 1);
                case 3: return (x + 1, y + 1);
                case 4: return (x + 1, y);
                case 5: return (x + 1, y - 1);
                case 6: return (x, y - 1);
                case 7: return (x - 1, y - 1);
                default: throw new IndexOutOfRangeException("Direction out of 0-7 range");
            }

        }
    }
}