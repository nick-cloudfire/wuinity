using System;
using System.Globalization;
using PREACT.Math;

namespace PREACT.Utility
{
    /// <summary>
    /// The area of interest picked on the map as two opposite corners: what the map's clicks mean, kept out of the
    /// Unity code so it can be tested.
    /// </summary>
    /// <remarks>
    /// On Auburn2 the pick logged "corner 1 of 2: 38.88812, -121.09182" and "corner 2 of 2: 38.88812, -121.09182" -
    /// one place clicked twice, a double click or a touchpad's double tap - and finished with an area of nothing, which
    /// the new-scenario dialog then showed as "not picked yet" with no word of why. A second corner that makes no area
    /// is not taken: the pick waits for the opposite corner and says so.
    /// </remarks>
    public sealed class AreaOfInterestPick
    {
        public enum Outcome
        {
            /// <summary>The first corner was taken; the pick waits for the opposite one.</summary>
            FirstCorner,

            /// <summary>The click was not taken as a corner (see <see cref="Message"/>); the pick still waits.</summary>
            Ignored,

            /// <summary>Both corners are in; <see cref="LowerLeft"/> and <see cref="UpperRight"/> hold the area.</summary>
            Done,
        }

        /// <summary>Smallest side the area may have, in metres: anything narrower is a click on the same spot or edge.</summary>
        public const double MinimumSideMetres = 100.0;

        /// <summary>A second click this soon after the first is a double click, and said to be one.</summary>
        public const double DoubleClickSeconds = 0.5;

        private Vector2d _first;
        private double _firstTime;

        /// <summary>How many corners are in: 0, 1 or 2.</summary>
        public int Corners { get; private set; }

        /// <summary>What to tell the user about the last click.</summary>
        public string Message { get; private set; } = string.Empty;

        public Vector2d First => _first;

        /// <summary>The south-west corner, (lat, lon), once <see cref="Corners"/> is 2.</summary>
        public Vector2d LowerLeft { get; private set; }

        /// <summary>The north-east corner, (lat, lon), once <see cref="Corners"/> is 2.</summary>
        public Vector2d UpperRight { get; private set; }

        /// <summary>Starts again: no corners.</summary>
        public void Reset()
        {
            Corners = 0;
            Message = string.Empty;
        }

        /// <summary>
        /// A click at <paramref name="latLon"/> ((lat, lon) as x, y) at <paramref name="timeSeconds"/> (any clock that
        /// only goes forward).
        /// </summary>
        public Outcome Click(Vector2d latLon, double timeSeconds)
        {
            if (Corners >= 2) Reset();

            if (Corners == 0)
            {
                _first = latLon;
                _firstTime = timeSeconds;
                Corners = 1;
                Message = $"Area of interest corner 1 of 2: {Format(latLon)}. Now click the opposite corner.";
                return Outcome.FirstCorner;
            }

            double northSouth = System.Math.Abs(latLon.x - _first.x) * 110574.0;
            double eastWest = System.Math.Abs(latLon.y - _first.y) * 111320.0
                              * System.Math.Cos(0.5 * (latLon.x + _first.x) * System.Math.PI / 180.0);

            if (northSouth < MinimumSideMetres || eastWest < MinimumSideMetres)
            {
                bool doubleClick = timeSeconds - _firstTime < DoubleClickSeconds;
                if (northSouth < MinimumSideMetres && eastWest < MinimumSideMetres)
                {
                    Message = doubleClick
                        ? $"That was a double click on corner 1 ({Format(_first)}), not a second corner. Click the opposite corner."
                        : $"Corner 2 cannot be where corner 1 is ({Format(_first)}). Click the opposite corner.";
                }
                else
                {
                    string narrow = northSouth < MinimumSideMetres ? "north-south" : "east-west";
                    Message = $"Those corners are only {System.Math.Min(northSouth, eastWest):F0} m apart {narrow}: click the corner "
                              + $"diagonally opposite {Format(_first)}, not one on the same edge.";
                }
                return Outcome.Ignored;
            }

            LowerLeft = new Vector2d(System.Math.Min(_first.x, latLon.x), System.Math.Min(_first.y, latLon.y));
            UpperRight = new Vector2d(System.Math.Max(_first.x, latLon.x), System.Math.Max(_first.y, latLon.y));
            Corners = 2;
            Message = $"Area of interest corner 2 of 2: {Format(latLon)} - {eastWest / 1000.0:F1} x {northSouth / 1000.0:F1} km.";
            return Outcome.Done;
        }

        private static string Format(Vector2d latLon)
        {
            return latLon.x.ToString("F5", CultureInfo.InvariantCulture) + ", " + latLon.y.ToString("F5", CultureInfo.InvariantCulture);
        }
    }
}
