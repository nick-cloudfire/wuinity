using PREACT.Math;
using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>The area of interest's two-corner pick on the map, without the map.</summary>
    internal static class AreaPickTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("area pick: a double click is not two corners; the pick waits for the opposite corner and says why", DoubleClickIsNotTwoCorners);
            runner.Add("area pick: corners in any order give south-west and north-east; a pick on one edge is refused; a new pick starts over", CornersInAnyOrder);
        }

        /// <summary>Nick's Auburn2 log: corner 1 and corner 2 both at 38.88812, -121.09182.</summary>
        private static void DoubleClickIsNotTwoCorners()
        {
            var pick = new AreaOfInterestPick();
            var spot = new Vector2d(38.88812, -121.09182);
            Assert.Equal(AreaOfInterestPick.Outcome.FirstCorner, pick.Click(spot, 10.00), "corner 1");
            Assert.True(pick.Message.StartsWith("Area of interest corner 1 of 2: 38.88812, -121.09182"), pick.Message);

            Assert.Equal(AreaOfInterestPick.Outcome.Ignored, pick.Click(spot, 10.15), "the same spot again, 0.15 s later");
            Assert.True(pick.Message.Contains("double click") && pick.Corners == 1, "said, and still waiting: " + pick.Message);

            Assert.Equal(AreaOfInterestPick.Outcome.Ignored, pick.Click(new Vector2d(38.88813, -121.09181), 14.0), "a slow second click on the spot");
            Assert.True(pick.Message.Contains("cannot be where corner 1 is"), pick.Message);

            //Then the opposite corner, as Nick clicked it the second time.
            Assert.Equal(AreaOfInterestPick.Outcome.Done, pick.Click(new Vector2d(38.81892, -120.94696), 20.0), "the opposite corner");
            Assert.True(pick.Corners == 2 && pick.Message.StartsWith("Area of interest corner 2 of 2: 38.81892, -120.94696"), pick.Message);
            Assert.Near(38.81892, pick.LowerLeft.x, 1e-9, "south");
            Assert.Near(-121.09182, pick.LowerLeft.y, 1e-9, "west");
            Assert.Near(38.88812, pick.UpperRight.x, 1e-9, "north");
            Assert.Near(-120.94696, pick.UpperRight.y, 1e-9, "east");
            Assert.True(pick.Message.Contains("12.6 x 7.7 km"), "the size is said: " + pick.Message);
        }

        private static void CornersInAnyOrder()
        {
            var pick = new AreaOfInterestPick();
            pick.Click(new Vector2d(38.98193, -121.19283), 0);
            Assert.Equal(AreaOfInterestPick.Outcome.Ignored, pick.Click(new Vector2d(38.98195, -120.94696), 5), "same latitude: one edge");
            Assert.True(pick.Message.Contains("north-south") && pick.Message.Contains("diagonally opposite"), pick.Message);
            Assert.Equal(AreaOfInterestPick.Outcome.Ignored, pick.Click(new Vector2d(38.81892, -121.19284), 6), "same longitude");
            Assert.True(pick.Message.Contains("east-west"), pick.Message);
            Assert.Equal(AreaOfInterestPick.Outcome.Done, pick.Click(new Vector2d(38.81892, -120.94696), 7), "north-west then south-east");
            Assert.True(pick.LowerLeft.x == 38.81892 && pick.LowerLeft.y == -121.19283 && pick.UpperRight.x == 38.98193 && pick.UpperRight.y == -120.94696,
                "ordered into south-west and north-east");

            //A click after a finished pick starts a new one.
            Assert.Equal(AreaOfInterestPick.Outcome.FirstCorner, pick.Click(new Vector2d(-33.9, 151.1), 30), "a new pick");
            Assert.Equal(AreaOfInterestPick.Outcome.Done, pick.Click(new Vector2d(-34.0, 151.3), 31), "southern hemisphere");
            Assert.True(pick.LowerLeft.x == -34.0 && pick.UpperRight.y == 151.3, "ordered there too");

            pick.Reset();
            Assert.True(pick.Corners == 0 && pick.Message.Length == 0, "reset");

            //Invariant digits, whatever the machine's locale.
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("el-GR");
                pick.Click(new Vector2d(38.5, 23.5), 0);
                Assert.True(pick.Message.Contains("38.50000, 23.50000"), pick.Message);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        }
    }
}
