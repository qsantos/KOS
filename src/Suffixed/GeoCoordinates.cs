﻿using UnityEngine;
using kOS.Utilities;

namespace kOS.Suffixed
{
    public class GeoCoordinates : SpecialValue
    {
        public double Lat { get; private set; }
        public double Lng { get; private set; }
        public CelestialBody Body { get; private set; }
        public SharedObjects Shared { get; set; } // for finding the current CPU's vessel, as per issue #107

        private const int TERRAIN_MASK_BIT = 15;

        /// <summary>
        ///   Build a GeoCoordinates from the current lat/long of the orbitable
        ///   object passed in.  The object being checked for should be in the same
        ///   SOI as the vessel running the CPU or the results are meaningless.
        /// </summary>
        /// <param name="orb">object to take current coords of</param>
        /// <param name="sharedObj">to know the current CPU's running vessel</param>
        public GeoCoordinates(Orbitable orb, SharedObjects sharedObj)
        {
            Shared = sharedObj;
            Vector p = orb.GetPosition();
            Lat = orb.PositionToLatitude(p);
            Lng = orb.PositionToLongitude(p);
            Body = orb.GetParentBody();
        }

        /// <summary>
        ///   Build a GeoCoordinates from any arbitrary lat/long pair of floats.
        /// </summary>
        /// <param name="sharedObj">to know the current CPU's running vessel</param>
        /// <param name="lat">latitude</param>
        /// <param name="lng">longitude</param>
        public GeoCoordinates(SharedObjects sharedObj, float lat, float lng)
        {
            Lat = lat;
            Lng = lng;
            Shared = sharedObj;
            Body = Shared.Vessel.GetOrbit().referenceBody;
        }

        /// <summary>
        ///   Build a GeoCoordinates from any arbitrary lat/long pair of doubles.
        /// </summary>
        /// <param name="sharedObj">to know the current CPU's running vessel</param>
        /// <param name="lat">latitude</param>
        /// <param name="lng">longitude</param>
        public GeoCoordinates(SharedObjects sharedObj, double lat, double lng)
        {
            Lat = lat;
            Lng = lng;
            Shared = sharedObj;
            Body = Shared.Vessel.GetOrbit().referenceBody;
        }

        /// <summary>
        ///   The bearing from the current CPU vessel to the surface spot with the
        ///   given lat/long coords, relative to the current CPU vessel's heading.
        /// </summary>
        /// <returns> bearing </returns>
        public double GetBearing()
        {
            return VesselUtils.AngleDelta(VesselUtils.GetHeading(Shared.Vessel), (float) GetHeadingFrom());
        }

        /// <summary>
        ///  Returns the ground's altitude above sea level at this geo position.
        /// </summary>
        /// <returns></returns>
        private double GetTerrainAltitude()
        {
            double alt = 0.0;
            PQS bodyPQS = Body.pqsController;
            if (bodyPQS != null) // The sun has no terrain.  Everthing else has a PQScontroller.
            {
                // The PQS controller gives the theoretical ideal smooth surface curve terrain.
                // The actual ground that exists in-game that you land on, however, is the terrain
                // polygon mesh which is built dynamically from the PQS controller's altitude values,
                // and it only approximates the PQS controller.  The discrepency between the two
                // can be as high as 20 meters on relatively mild rolling terrain and is probably worse
                // in mountainous terrain with steeper slopes.  It also varies with the user terrain detail
                // graphics setting.

                // Therefore the algorithm here is this:  Get the PQS ideal terrain altitude first.
                // Then try using RayCast to get the actual terrain altitude, which will only work
                // if the LAT/LONG is near the active vessel so the relevant terrain polygons are
                // loaded.  If the RayCast hit works, it overrides the PQS altitude.
                                
                // PQS controller ideal altitude value:
                // -------------------------------------

                // The vector the pqs GetSurfaceHeight method expects is a vector in the following
                // reference frame:
                //     Origin = body center.
                //     X axis = LATLNG(0,0), Y axis = LATLNG(90,0)(north pole), Z axis = LATLNG(0,-90).
                // Using that reference frame, you tell GetSurfaceHeight what the "up" vector is pointing through
                // the spot on the surface you're querying for.
                var bodyUpVector = new Vector3d(1,0,0);
                bodyUpVector = QuaternionD.AngleAxis(Lat, Vector3d.forward/*around Z axis*/) * bodyUpVector;
                bodyUpVector = QuaternionD.AngleAxis(Lng, Vector3d.down/*around -Y axis*/) * bodyUpVector;

                alt = bodyPQS.GetSurfaceHeight( bodyUpVector ) - bodyPQS.radius ;

                // Terrain polygon raycasting:
                // ---------------------------
                const double HIGH_AGL = 1000.0;
                const double POINT_AGL = 800.0;
                // a point hopefully above the terrain:
                Vector3d worldRayCastStart = Body.GetWorldSurfacePosition( Lat, Lng, alt+HIGH_AGL );
                // a point a bit below it, to aim down to the terrain:
                Vector3d worldRayCastStop = Body.GetWorldSurfacePosition( Lat, Lng, alt+POINT_AGL );
                RaycastHit hit;
                if (Physics.Raycast(worldRayCastStart, (worldRayCastStop - worldRayCastStart), out hit, 1<<TERRAIN_MASK_BIT ))
                {
                    // Ensure hit is on the topside of planet, near the worldRayCastStart, not on the far side.
                    if (Mathf.Abs(hit.distance) < 3000)
                    {
                        // Okay a hit was found, use it instead of PQS alt:
                        alt = ((alt+HIGH_AGL) - hit.distance);
                    }
                }
            }
            return alt;
        }

        /// <summary>
        ///   The compass heading from the current position of the CPU vessel to the
        ///   LAT/LANG position on the SOI body's surface.
        /// </summary>
        /// <returns>compass heading in degrees</returns>
        private double GetHeadingFrom()
        {
            var up = Shared.Vessel.upAxis;
            var north = VesselUtils.GetNorthVector(Shared.Vessel);

            var targetWorldCoords = Body.GetWorldSurfacePosition(Lat, Lng, GetTerrainAltitude() );

            var vector = Vector3d.Exclude(up, targetWorldCoords - Shared.Vessel.GetWorldPos3D()).normalized;
            var headingQ =
                Quaternion.Inverse(Quaternion.Euler(90, 0, 0)*Quaternion.Inverse(Quaternion.LookRotation(vector, up))*
                                   Quaternion.LookRotation(north, up));

            return headingQ.eulerAngles.y;
        }

        /// <summary>
        ///   The distance of the surface point of this LAT/LONG from where
        ///   the current CPU vessel is now.
        /// </summary>
        /// <returns>distance scalar</returns>
        private double DistanceFrom()
        {
            Vector3d latLongCoords = Body.GetWorldSurfacePosition( Lat, Lng, GetTerrainAltitude() );
            Vector3d hereCoords = Shared.Vessel.GetWorldPos3D();
            return Vector3d.Distance( latLongCoords, hereCoords );
        }

        public override object GetSuffix(string suffixName)
        {
            switch (suffixName)
            {
                case "LAT":
                    return Lat;
                case "LNG":
                    return Lng;
                case "BODY":
                    return new BodyTarget( Body, Shared );
                case "TERRAINHEIGHT":
                    return GetTerrainAltitude();
                case "DISTANCE":
                    return DistanceFrom();
                case "HEADING":
                    return GetHeadingFrom();
                case "BEARING":
                    return GetBearing();
            }

            return base.GetSuffix(suffixName);
        }

        public override string ToString()
        {
            return "LATLNG(" + Lat + ", " + Lng + ")";
        }
    }
}
