//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using PREACT.Math;

namespace PREACT.Dispersion
{
    public abstract class SmokeModule : SimulationModule
    {
        public SmokeModule(Simulation simulation) : base(simulation)
        {

        }
        public abstract int GetCellsX();
        public abstract int GetCellsY();

        /// <summary>
        /// Light extinction coefficient in 1/m on the module's grid at ground level, linearised with x leading
        /// (<see cref="GetCellsX"/> by <see cref="GetCellsY"/>): what the smoke overlay draws.
        /// </summary>
        /// <remarks>
        /// Everything smoke is used for is expressed as an extinction coefficient - visibility and the SUMO speed
        /// reduction - so that is what a module hands out. The soot-density members this replaces had no reader
        /// but the overlay, and GlobalSmoke (whose input is an extinction coefficient) returned its ramp value
        /// from them anyway. A module that transports soot converts with its own mass-specific extinction
        /// (8700 m2/kg for flaming combustion, Mulholland &amp; Croarkin).
        /// </remarks>
        public abstract float[] GetExtinctionCoefficientData();

        /// <summary>Light extinction coefficient in 1/m at a simulation position.</summary>
        public abstract float GetExtinctionCoefficientAtPos(Vector2d pos);
    }

}

