//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using PREACT.Math;

namespace PREACT.Wildfire
{
    public abstract class WildfireModule : SimulationModule
    {
        protected double _internalDeltaTime;

        protected double _currentBurnArea;
        public double CurrentBurnArea { get => _currentBurnArea; }

        public WildfireModule(Simulation simulation) : base(simulation)
        {

        }

        /// <summary>
        /// The fire module might be able to take longer time steps compared to other modules, so this information is needed if only doing fire simulation.
        /// </summary>
        /// <returns></returns>
        public abstract double GetInternalDeltaTime();

        /// <summary>
        /// Get the maximum rate of spread (any direction, so azimuth is also needed to back calculate eliipse)
        /// </summary>
        public abstract float[,] GetMaxROS();
        /// <summary>
        /// Get the spread direction of the maximum rate of spread, 0 degrees is North and then clockwise
        /// </summary>
        public abstract float[,] GetMaxROSAzimuth();

        public abstract int GetCellCountX();
        public abstract int GetCellCountY();
        public abstract float GetCellSizeX();
        public abstract float GetCellSizeY();
        public abstract float[] GetFireLineIntensityData();
        public abstract float[] GetFuelModelNumberData();
        public abstract float[] GetSootProduction();
        public abstract int GetActiveCellCount();
        public abstract List<Vector2int> GetIgnitedFireCells();
        public abstract void ConsumeIgnitedFireCells();
        public abstract bool Ignited();

        /// <summary>
        /// Whether the fire ever reached this cell during the simulated period.
        /// </summary>
        /// <remarks>
        /// Asked by the trigger boundary, which is only meaningful for an area a fire actually threatens. It
        /// is a question about the whole run rather than about now, so it stays true once the cell has burned
        /// - unlike <see cref="GetFireCellState"/>, which describes the current moment.
        /// </remarks>
        public abstract bool CellHasBurned(int x, int y);

        /// <summary>
        /// When the fire reached this cell, in seconds from the start of the simulation, or
        /// <see cref="float.MaxValue"/> if it never did or the cell is outside the grid.
        /// </summary>
        /// <remarks>
        /// Exposed for k-PERIL, which has no time axis and so has to collapse a time-varying wind field to
        /// one value per cell: the wind at the hour the fire actually arrived there. <see cref="CellHasBurned"/>
        /// answered whether, but not when, and "when" is what selects the band.
        /// </remarks>
        public abstract float GetTimeOfArrival(int x, int y);

        public abstract void GetOffsetAndSize(out Vector2d offset, out Vector2d size);

        /// <summary>
        /// The fire grid's south-west corner in the case's own projected coordinates, or (0,0) when the module
        /// does not know one.
        /// </summary>
        /// <remarks>
        /// Needed so a raster derived from this grid — a trigger boundary above all — can be written where the
        /// ground it describes actually is. k-PERIL's output used to be written with a hardcoded origin of
        /// (0, 0), which made every boundary, and the campaign's aggregated probability raster with it,
        /// impossible to overlay on the domain without repositioning it by hand.
        /// </remarks>
        public virtual Vector2d GetGridOriginUtm()
        {
            return Vector2d.zero;
        }

        /// <summary>
        /// Returns state of cell on mesh based on simulation position. Returns dead if outside of mesh.
        /// </summary>
        /// <param name="simulationPos"></param>
        /// <returns></returns>
        public abstract FireCellState GetFireCellState(Vector2d simulationPos);

        public abstract Vector2int SimulationPosToCellIndex(Vector2d simulationPos, out bool inside);
    }
}

