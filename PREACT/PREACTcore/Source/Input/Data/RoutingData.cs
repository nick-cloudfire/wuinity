//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using Itinero;
using System.IO;
using Itinero.IO.Osm;
using Itinero.Osm.Vehicles;
using OsmSharp.Streams;
using PREACT.Math;

namespace PREACT.Input
{
    public static class RoutingData
    {
        /// <summary>
        /// Resolve a lat/lon to the nearest valid point on the road network, searching
        /// within the diagonal radius of a cell. Returns null if nothing is within range.
        /// </summary>
        public static RouterPoint GetValidRouterPoint(Router router, Vector2d latLon, Itinero.Profiles.Profile p, float cellSize)
        {
            //check within the radius of the diagonal of the cell (so complete cell plus some parts of neighboring cells)
            RouterPoint start = null;
            try
            {
                start = router.Resolve(p, (float)latLon.x, (float)latLon.y, cellSize * 0.70711f); //half cell size * sqrt 2
                //for some reason Itinero does not return the actual point on the network, so we have to get it and overwrite
                Itinero.LocalGeo.Coordinate temp = start.LocationOnNetwork(router.Db);
                start = new RouterPoint(temp.Latitude, temp.Longitude, start.EdgeId, start.Offset);
            }
            catch (Itinero.Exceptions.ResolveFailedException)
            {
                //point is too far from the road network
            }

            return start;
        }

        public static RouterDb LoadRouterDb(string filePath, out bool success)
        {
            success = false;

            RouterDb routerDb = null;
            if (File.Exists(filePath))
            {
                using (FileStream stream = new FileInfo(filePath).OpenRead())
                {
                    routerDb = RouterDb.Deserialize(stream);
                    success = true;
                }
            }

            if (success)
            {
                //some road networks returns zero routes without this contract being signed (especially Swedish road networks)...
                routerDb.AddContracted(routerDb.GetSupportedProfile("Car"));
                Engine.Message(null, Engine.LogType.Log, "Router database loaded succesfully.");
                
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, "Router database could not be found.");
            }

            return routerDb;
        }

        public static RouterDb CreateRouterDb(string osmInputFilePath, out bool success)
        {
            success = false;
            RouterDb routerDb = null;

            if (File.Exists(osmInputFilePath))
            {
                //stream in data from OSM
                using (FileStream stream = new FileInfo(osmInputFilePath).OpenRead())
                {
                    OsmStreamSource source;
                    if (osmInputFilePath.ToLower().EndsWith("pbf"))
                    {
                        source = new PBFOsmStreamSource(stream);
                    }
                    else
                    {
                        source = new XmlOsmStreamSource(stream);
                    }

                    // create the network for cars only.
                    LoadSettings settings = new LoadSettings();
                    settings.KeepNodeIds = true; //use to enable measure flow at nodes
                    settings.KeepWayIds = true; //can be used to calc density easier?
                    settings.OptimizeNetwork = true;

                    //build db from OSM betwork, TODO: allocate och heap instead? keep track                    
                    routerDb = new RouterDb();
                    routerDb.LoadOsmData(source, settings, Vehicle.Car);
                    success = true;
                }                
            }

            return routerDb;
        }

        public static void CreateAndSaveRouterDb(string osmInputFilePath, string outputFilePath, out bool success)
        {
            success = false;

            if (File.Exists(osmInputFilePath))
            {                
                RouterDb routerDb = CreateRouterDb(osmInputFilePath, out success);
                if (success)
                {
                    // write the new routerdb to disk.
                    using (FileStream outputStream = new FileInfo(outputFilePath).Open(FileMode.Create))
                    {
                        routerDb.Serialize(outputStream);
                        Engine.Message(null, Engine.LogType.Log, "Router database saved to file " + outputFilePath);
                    }

                    success = true;
                }
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, "OSM file could not be found.");
            }
        }
    }
}