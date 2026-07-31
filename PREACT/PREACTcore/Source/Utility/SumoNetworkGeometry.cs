using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using PREACT.Math;

namespace PREACT.Utility
{
    /// <summary>
    /// The SUMO network's lane geometry, in the frame the simulation places everything else in.
    ///
    /// Two things need this. Drawing the road network, so what is being authored can be seen against the
    /// roads rather than against a satellite image alone; and placing evacuation destinations, which have to
    /// sit on a lane. SUMO resolves a destination to an edge at run time with convertRoad, which never
    /// fails and never reports a distance - a destination dropped a hundred metres into a field silently
    /// becomes whichever edge happens to be nearest, and the first sign of it is traffic leaving by the
    /// wrong road. Snapping when it is placed makes that choice visible and correctable.
    ///
    /// The transform is the one SUMOModule uses, so a position snapped here is the position SUMO will
    /// resolve at run time:
    ///
    ///     simulation = netCoordinate - netOffset - utmOrigin
    ///
    /// SUMO writes its network with an offset from the original projected coordinates, and everything in
    /// WUInity is measured from the simulation's own UTM origin. Get either wrong and the network is drawn
    /// somewhere in the next municipality.
    /// </summary>
    public class SumoNetworkGeometry
    {
        /// <summary>One lane, as a polyline in simulation coordinates.</summary>
        public class Lane
        {
            public string Id;
            public string EdgeId;
            public Vector2d[] Points;
        }

        private readonly List<Lane> _lanes = new List<Lane>();
        public IReadOnlyList<Lane> Lanes { get => _lanes; }
        public int LaneCount { get => _lanes.Count; }

        /// <summary>Where a snapped position ended up, and on what.</summary>
        public struct Snap
        {
            public string LaneId;
            public string EdgeId;
            public Vector2d SimulationPos;
            /// <summary>How far the position moved, in metres. The number worth showing a user.</summary>
            public double Distance;
        }

        private SumoNetworkGeometry()
        {
        }

        /// <summary>
        /// Reads the network the given SUMO configuration points at. Returns null, with the reason logged,
        /// when there is no configuration, no network, or nothing drivable in it.
        /// </summary>
        /// <param name="configurationPath">Path to the .sumocfg.</param>
        /// <param name="utmOrigin">The simulation's UTM origin, which its own coordinates are measured from.</param>
        public static SumoNetworkGeometry Load(string configurationPath, Vector2d utmOrigin)
        {
            if (string.IsNullOrWhiteSpace(configurationPath) || !File.Exists(configurationPath))
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "No SUMO configuration to read a road network from"
                    + (string.IsNullOrWhiteSpace(configurationPath) ? "." : ": " + configurationPath));
                return null;
            }

            //A .sumocfg names a network; a .net.xml is one. Both are accepted, because both are things a
            //user reasonably has in hand, and the scenario field that holds this is a free path.
            string lower = configurationPath.ToLowerInvariant();
            bool isNetwork = lower.EndsWith(".net.xml") || lower.EndsWith(".net.xml.gz");
            bool isConfiguration = lower.EndsWith(".sumocfg") || lower.EndsWith(".sumo.cfg");

            if (!isNetwork && !isConfiguration)
            {
                //Named rather than attempted. An OSM extract is the file most likely to be here by mistake -
                //it is in the scenario folder, it is what the network was built from, and the file picker
                //that sets this asks for a "SUMO input file" - and parsing it as a network fails somewhere
                //deep with a message about a missing <input> element that explains none of that.
                Engine.Message(null, Engine.LogType.Warning,
                    Path.GetFileName(configurationPath) + " is not a SUMO network. The scenario's SUMO "
                    + "ConfigurationFile has to be the .sumocfg netconvert wrote (usually sumo/osm.sumocfg) or a "
                    + ".net.xml, not the OSM extract it was built from.");
                return null;
            }

            string networkPath = isNetwork ? configurationPath : ResolveNetworkFile(configurationPath);
            if (networkPath == null)
            {
                return null;
            }

            SumoNetworkGeometry geometry;
            try
            {
                geometry = ReadNetwork(networkPath, utmOrigin);
            }
            catch (Exception exception)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "Could not read the SUMO network: " + exception.Message);
                return null;
            }

            if (geometry == null || geometry._lanes.Count == 0)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "The SUMO network holds no lane with a shape to draw. Rebuild it from the OSM data.");
                return null;
            }

            Engine.Message(null, Engine.LogType.Log,
                $"Read {geometry._lanes.Count} lanes from {Path.GetFileName(networkPath)}.");
            return geometry;
        }

        /// <summary>The net-file a .sumocfg names, resolved against the configuration's own folder.</summary>
        private static string ResolveNetworkFile(string configurationPath)
        {
            using (XmlReader reader = XmlReader.Create(configurationPath, ReaderSettings()))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "net-file")
                    {
                        continue;
                    }

                    string value = reader.GetAttribute("value");
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        break;
                    }

                    string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationPath)), value);
                    if (!File.Exists(path))
                    {
                        Engine.Message(null, Engine.LogType.Warning,
                            "The SUMO configuration names a network that is not there: " + path);
                        return null;
                    }
                    return path;
                }
            }

            Engine.Message(null, Engine.LogType.Warning,
                Path.GetFileName(configurationPath) + " names no net-file, so there is no network to read.");
            return null;
        }

        /// <summary>
        /// Streams the lane shapes out of a .net.xml.
        ///
        /// Streamed rather than read into an XmlDocument, which is what SumoParser does. A town's network is
        /// a hundred megabytes and a hundred and fifty thousand lanes - Mati's is 96 MB - and a DOM of that
        /// costs the better part of a gigabyte and several seconds to build, for a file this only needs to
        /// walk once. It also keeps only what is wanted: junctions, connections and traffic light logic are
        /// most of the file and none of the geometry.
        /// </summary>
        private static SumoNetworkGeometry ReadNetwork(string networkPath, Vector2d utmOrigin)
        {
            var geometry = new SumoNetworkGeometry();
            Vector2d offset = Vector2d.zero;
            bool haveOffset = false;

            Stream stream = File.OpenRead(networkPath);
            try
            {
                if (networkPath.ToLowerInvariant().EndsWith(".gz"))
                {
                    stream = new GZipStream(stream, CompressionMode.Decompress);
                }

                using (XmlReader reader = XmlReader.Create(stream, ReaderSettings()))
                {
                    string edgeId = null;
                    bool skipEdge = false;

                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "edge")
                        {
                            edgeId = null;
                            skipEdge = false;
                            continue;
                        }

                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        if (reader.Name == "location")
                        {
                            //netOffset is what SUMO added to the projected coordinates, so subtracting it
                            //returns UTM, and subtracting the simulation's origin puts it in simulation
                            //space. The same arithmetic SUMOModule does, so the drawn network and the
                            //running one cannot drift apart.
                            string netOffset = reader.GetAttribute("netOffset");
                            if (!string.IsNullOrWhiteSpace(netOffset))
                            {
                                string[] parts = netOffset.Split(',');
                                if (parts.Length == 2
                                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ox)
                                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double oy))
                                {
                                    offset = new Vector2d(-ox - utmOrigin.x, -oy - utmOrigin.y);
                                    haveOffset = true;
                                }
                            }
                            continue;
                        }

                        if (reader.Name == "edge")
                        {
                            edgeId = reader.GetAttribute("id");
                            string function = reader.GetAttribute("function");
                            //Internal edges are the geometry inside junctions - they start with ':' and carry
                            //a function - and crossings and walking areas are not roads vehicles drive on.
                            skipEdge = edgeId == null || edgeId.StartsWith(":") || !string.IsNullOrEmpty(function);
                            continue;
                        }

                        if (reader.Name != "lane" || skipEdge || edgeId == null)
                        {
                            continue;
                        }

                        Vector2d[] points = ParseShape(reader.GetAttribute("shape"), offset);
                        //A lane with fewer than two points has no direction and can be neither drawn nor
                        //projected onto. netconvert does write such shapes.
                        if (points == null)
                        {
                            continue;
                        }

                        geometry._lanes.Add(new Lane
                        {
                            Id = reader.GetAttribute("id"),
                            EdgeId = edgeId,
                            Points = points
                        });
                    }
                }
            }
            finally
            {
                stream.Dispose();
            }

            if (!haveOffset)
            {
                //Fatal rather than assumed zero: netOffset appears before any edge, so its absence means the
                //file is not a network - and a zero offset would put the whole network millions of metres out
                //without anything looking wrong about it.
                Engine.Message(null, Engine.LogType.Warning,
                    Path.GetFileName(networkPath) + " has no <location netOffset>, so its coordinates cannot be "
                    + "placed. It is probably not a SUMO network.");
                return null;
            }

            return geometry;
        }

        private static XmlReaderSettings ReaderSettings()
        {
            return new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
                //Nothing here needs external entities, and resolving them on a file from elsewhere is how
                //XML parsers get turned into a way to read the rest of the disk.
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
        }

        private static Vector2d[] ParseShape(string shape, Vector2d offset)
        {
            if (string.IsNullOrEmpty(shape))
            {
                return null;
            }

            string[] pairs = shape.Split(' ');
            var points = new List<Vector2d>(pairs.Length);

            foreach (string pair in pairs)
            {
                string[] xy = pair.Split(',');
                //Three components where the network carries elevation; the third is ignored rather than
                //rejected, since these are drawn and measured flat.
                if (xy.Length < 2
                    || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                {
                    continue;
                }

                points.Add(new Vector2d(x + offset.x, y + offset.y));
            }

            return points.Count < 2 ? null : points.ToArray();
        }

        /// <summary>
        /// The nearest point on any lane to <paramref name="simulationPos"/>.
        ///
        /// Nearest by perpendicular distance to a lane's segments, not by distance to its shape points: a
        /// straight kilometre of road is two points, and comparing against those alone would send a
        /// destination beside its middle to whichever cross street happened to have a node nearby.
        /// </summary>
        public bool TrySnap(Vector2d simulationPos, out Snap snap)
        {
            snap = new Snap();
            double bestSqr = double.MaxValue;
            bool found = false;

            foreach (Lane lane in _lanes)
            {
                for (int i = 0; i < lane.Points.Length - 1; ++i)
                {
                    Vector2d closest = ClosestPointOnSegment(lane.Points[i], lane.Points[i + 1], simulationPos);
                    double dx = closest.x - simulationPos.x;
                    double dy = closest.y - simulationPos.y;
                    double sqr = dx * dx + dy * dy;

                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        snap.LaneId = lane.Id;
                        snap.EdgeId = lane.EdgeId;
                        snap.SimulationPos = closest;
                        found = true;
                    }
                }
            }

            if (found)
            {
                snap.Distance = System.Math.Sqrt(bestSqr);
            }

            return found;
        }

        private static Vector2d ClosestPointOnSegment(Vector2d a, Vector2d b, Vector2d p)
        {
            double abx = b.x - a.x;
            double aby = b.y - a.y;
            double lengthSqr = abx * abx + aby * aby;

            //A zero-length segment is its own closest point; the projection below would divide by zero.
            if (lengthSqr <= double.Epsilon)
            {
                return a;
            }

            double t = ((p.x - a.x) * abx + (p.y - a.y) * aby) / lengthSqr;
            t = System.Math.Max(0.0, System.Math.Min(1.0, t));

            return new Vector2d(a.x + t * abx, a.y + t * aby);
        }
    }
}
