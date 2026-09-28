//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using PREACT.Math;

namespace PREACT.Dispersion
{
    /// <summary>
    /// One smoke value for the whole domain, read from a time ramp (<c>[GlobalSmoke] ExtinctionFile</c>: a header,
    /// then <c>time [s], extinction coefficient [1/m]</c> rows).
    /// </summary>
    /// <remarks>
    /// The ramp holds an <b>extinction coefficient</b>. It used to be handed out as a soot density and multiplied
    /// by 8700 m2/kg on the way to the SUMO speed reduction, so the speed model saw K x 8700 - 1740 /m for
    /// Roxborough's 0.2 /m. <see cref="GetExtinctionCoefficientAtPos"/> and the overlay's
    /// <see cref="GetExtinctionCoefficientData"/> return the ramp value itself.
    /// </remarks>
    public class GlobalSmoke : SmokeModule
    {
        ExtinctionRamp _extinctionCoefficientRamp;
        float[] _extinctionCoefficientOutput;

        public GlobalSmoke(Simulation simulation, ExtinctionRamp extinctionCoefficientRamp) : base(simulation)
        {
            _extinctionCoefficientRamp = extinctionCoefficientRamp;
            _extinctionCoefficientOutput = new float[1];
        }


        public override void Step(double currentTime, double deltaTime)
        {
            _extinctionCoefficientOutput[0] = _extinctionCoefficientRamp.GetOpticalDensity((float)currentTime);
        }      

        public override bool IsSimulationDone()
        {
            return false;
        }

        public override int GetCellsX()
        {
            return 1;
        }

        public override int GetCellsY()
        {
            return 1;
        }

        /// <summary>One cell: the domain's extinction coefficient now, 1/m.</summary>
        public override float[] GetExtinctionCoefficientData()
        {
            return _extinctionCoefficientOutput;
        }

        public override float GetExtinctionCoefficientAtPos(Vector2d pos)
        {
            return _extinctionCoefficientOutput[0];
        }

        public override void Stop()
        {

        }
    }
}

