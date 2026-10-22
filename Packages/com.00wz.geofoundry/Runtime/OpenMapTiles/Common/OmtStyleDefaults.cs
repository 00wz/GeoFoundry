using System.Collections.Generic;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Default style tables of the OpenMapTiles handlers, transcribed from the official "OSM OpenMapTiles" style
    /// (<c>layers/*/style.json</c> of the openmaptiles repository, CC-BY 4.0). Colors are copied verbatim; pixel widths
    /// are the style's widths around zoom 10–12 used as on-screen minimums, real widths are typical ground sizes.
    /// Semi-transparent style colors keep their alpha (the style's fill/line opacity): the draped shaders blend by it.
    /// Fill patterns of the style (forest leaves, wetland reeds, military hatching) are not reproduced.
    /// </summary>
    public static class OmtStyleDefaults
    {
        /// <summary>The style's background color, used for the terrain material and fades toward the background.</summary>
        public static Color Background => C("hsl(40, 26%, 93%)");

        private static Color C(string css) => OmtColor.Parse(css);

        /// <summary>A style color with the style's opacity as alpha.</summary>
        private static Color A(string css, float opacity)
        {
            var c = C(css);
            c.a *= opacity;
            return c;
        }

        // ---- water ------------------------------------------------------------

        public static List<OmtFillRule> WaterFills() => new List<OmtFillRule>
        {
            new OmtFillRule("", "", C("hsl(194, 45%, 77%)"), 53),
        };

        /// <summary>Color of intermittent water polygons (<c>intermittent=1</c>).</summary>
        public static Color WaterIntermittent => A("hsl(205, 91%, 83%)", 0.85f);

        // ---- landcover --------------------------------------------------------

        public static List<OmtFillRule> LandcoverFills()
        {
            Color wood = C("hsl(102, 36%, 72%)"), grass = C("hsl(91, 60%, 81%)"), sand = C("hsl(45, 70%, 87%)");
            Color rock = C("hsl(30, 35%, 90%)"), farmland = C("hsl(64, 47%, 89%)"), scrub = C("hsl(80, 35%, 76%)");
            Color orchard = C("hsl(109, 48%, 76%)"), heath = C("hsl(63, 43%, 74%)");
            return new List<OmtFillRule>
            {
                // Subclass-specific (style layers 19–43, zoom 9+).
                new OmtFillRule("", "park", C("hsl(125, 83%, 88%)"), 20, 13),
                new OmtFillRule("", "garden", grass, 23, 9),
                new OmtFillRule("", "flowerbed", grass, 23, 9),
                new OmtFillRule("", "wood", wood, 25, 9),
                new OmtFillRule("", "forest", wood, 35, 9),
                new OmtFillRule("", "tidalflat", C("hsl(28, 19%, 84%)"), 26, 9),
                new OmtFillRule("", "wetland", wood, 27, 9),
                new OmtFillRule("", "swamp", wood, 27, 9),
                new OmtFillRule("", "bog", wood, 27, 9),
                new OmtFillRule("", "marsh", wood, 27, 9),
                new OmtFillRule("", "scree", rock, 28, 9),
                new OmtFillRule("", "bare_rock", rock, 41, 9),
                new OmtFillRule("", "sand", sand, 29, 9),
                new OmtFillRule("", "dune", sand, 39, 9),
                new OmtFillRule("", "beach", C("hsl(48, 100%, 86%)"), 40, 9),
                new OmtFillRule("", "recreation_ground", C("hsl(126, 100%, 92%)"), 30, 9),
                new OmtFillRule("", "orchard", orchard, 31, 9),
                new OmtFillRule("", "plant_nursery", orchard, 31, 9),
                new OmtFillRule("", "vineyard", orchard, 31, 9),
                new OmtFillRule("", "meadow", grass, 32, 9),
                new OmtFillRule("", "wet_meadow", grass, 32, 9),
                new OmtFillRule("", "reedbed", grass, 32, 9),
                new OmtFillRule("", "saltmarsh", grass, 32, 9),
                new OmtFillRule("", "village_green", grass, 32, 9),
                new OmtFillRule("", "mangrove", scrub, 33, 9),
                new OmtFillRule("", "scrub", scrub, 38, 9),
                new OmtFillRule("", "shrubbery", scrub, 38, 9),
                new OmtFillRule("", "heath", heath, 34, 9),
                new OmtFillRule("", "fell", heath, 34, 9),
                new OmtFillRule("", "tundra", heath, 34, 9),
                new OmtFillRule("", "farmland", farmland, 36, 9),
                new OmtFillRule("", "farm", C("hsl(35, 75%, 85%)"), 37, 9),
                new OmtFillRule("", "allotments", C("hsl(102, 36%, 82%)"), 42, 9),
                new OmtFillRule("", "grass", grass, 43, 9),
                new OmtFillRule("", "grassland", grass, 43, 9),
                new OmtFillRule("", "golf_course", grass, 43, 9),
                new OmtFillRule("", "saltern", C("hsl(194, 25%, 85%)"), 32, 9),
                // Class fallbacks (medium scale, style layers 13–18 and 45).
                new OmtFillRule("ice", "", C("hsl(180, 28%, 90%)"), 45),
                new OmtFillRule("wetland", "", wood, 13),
                new OmtFillRule("sand", "", sand, 14),
                new OmtFillRule("grass", "", grass, 15),
                new OmtFillRule("rock", "", rock, 16),
                new OmtFillRule("wood", "", wood, 17),
                new OmtFillRule("farmland", "", farmland, 18),
            };
        }

        public static List<OmtLineRule> LandcoverOutlines() => new List<OmtLineRule>
        {
            new OmtLineRule("", "allotments", C("hsl(102, 21%, 72%)"), 0f, 0.5f, 44, 14),
            new OmtLineRule("", "farm", C("hsl(35, 43%, 68%)"), 0f, 0.5f, 44, 14),
            new OmtLineRule("", "farmland", C("hsl(64, 20%, 74%)"), 0f, 0.5f, 44, 14),
            new OmtLineRule("", "recreation_ground", C("hsl(126, 26%, 32%)"), 0f, 0.3f, 44, 14),
            new OmtLineRule("ice", "", C("hsl(210, 100%, 80%)"), 0f, 1.2f, 46, 5).WithDash(4, 2),
        };

        // ---- landuse ----------------------------------------------------------

        public static List<OmtFillRule> LanduseFills()
        {
            Color residential = C("hsl(0, 2%, 88%)"), education = C("hsl(60, 100%, 95%)");
            Color industrial = C("hsl(311, 29%, 89%)"), stadium = C("hsl(126, 100%, 92%)"), pitch = C("hsl(157, 47%, 77%)");
            return new List<OmtFillRule>
            {
                new OmtFillRule("residential", "", residential, 1, 6),
                new OmtFillRule("suburb", "", residential, 1, 6),
                new OmtFillRule("quarter", "", residential, 1, 6),
                new OmtFillRule("neighbourhood", "", residential, 1, 6),
                new OmtFillRule("cemetery", "", C("hsl(129, 24%, 73%)"), 2, 7),
                new OmtFillRule("military", "", C("hsl(0, 75%, 95%)"), 3, 7),
                new OmtFillRule("railway", "", industrial, 4, 7),
                new OmtFillRule("garages", "", C("hsl(53, 21%, 84%)"), 5, 7),
                new OmtFillRule("dam", "", C("hsl(0, 0%, 68%)"), 6, 7),
                new OmtFillRule("quarry", "", C("hsl(0, 0%, 83%)"), 7, 7),
                new OmtFillRule("industrial", "", industrial, 8, 7),
                new OmtFillRule("retail", "", C("hsl(7, 100%, 91%)"), 9, 7),
                new OmtFillRule("commercial", "", C("hsl(2, 49%, 90%)"), 10, 7),
                new OmtFillRule("bus_station", "", C("hsl(0, 0%, 88%)"), 10, 12),
                new OmtFillRule("parking", "", C("hsl(0, 0%, 93%)"), 10, 13),
                new OmtFillRule("school", "", education, 11, 7),
                new OmtFillRule("university", "", education, 11, 7),
                new OmtFillRule("kindergarten", "", education, 11, 7),
                new OmtFillRule("college", "", education, 11, 7),
                new OmtFillRule("hospital", "", education, 11, 7),
                new OmtFillRule("library", "", education, 11, 7),
                new OmtFillRule("stadium", "", stadium, 21, 7),
                new OmtFillRule("playground", "", stadium, 21, 7),
                new OmtFillRule("pitch", "", pitch, 22, 7),
                new OmtFillRule("track", "", pitch, 22, 7),
                // Outlined only in the style.
                new OmtFillRule("theme_park", "", C("hsl(0, 0%, 0%)"), 0) { Visible = false },
                new OmtFillRule("zoo", "", C("hsl(0, 0%, 0%)"), 0) { Visible = false },
            };
        }

        public static List<OmtLineRule> LanduseOutlines()
        {
            Color edu = C("hsl(60, 13%, 60%)");
            return new List<OmtLineRule>
            {
                new OmtLineRule("railway", "", C("hsl(309, 14%, 74%)"), 0f, 0.7f, 60, 13),
                new OmtLineRule("military", "", A("hsl(0, 100%, 67%)", 0.24f), 0f, 2f, 60, 13),
                new OmtLineRule("residential", "", C("hsl(0, 0%, 73%)"), 0f, 0.5f, 60, 13),
                new OmtLineRule("industrial", "", C("hsl(309, 14%, 74%)"), 0f, 0.5f, 60, 13),
                new OmtLineRule("retail", "", C("hsl(6, 47%, 72%)"), 0f, 0.5f, 60, 13),
                new OmtLineRule("school", "", edu, 0f, 0.3f, 60, 13),
                new OmtLineRule("university", "", edu, 0f, 0.3f, 60, 13),
                new OmtLineRule("kindergarten", "", edu, 0f, 0.3f, 60, 13),
                new OmtLineRule("college", "", edu, 0f, 0.3f, 60, 13),
                new OmtLineRule("hospital", "", edu, 0f, 0.3f, 60, 13),
                new OmtLineRule("stadium", "", C("hsl(126, 19%, 57%)"), 0f, 0.3f, 60, 13),
                new OmtLineRule("pitch", "", C("hsl(156, 22%, 57%)"), 0f, 0.5f, 60, 13),
                new OmtLineRule("playground", "", C("hsl(126, 26%, 32%)"), 0f, 0.3f, 60, 13),
                new OmtLineRule("track", "", C("hsl(155, 22%, 57%)"), 0f, 0.5f, 60, 13),
                new OmtLineRule("theme_park", "", A("hsl(330, 100%, 20%)", 0.3f), 0f, 3f, 61, 13),
                new OmtLineRule("zoo", "", A("hsl(330, 100%, 20%)", 0.3f), 0f, 3f, 61, 13),
                new OmtLineRule("dam", "", C("hsl(0, 0%, 27%)"), 0f, 2f, 60, 13),
            };
        }

        // ---- park -------------------------------------------------------------

        /// <summary>
        /// The style draws parks as outlines only; a faint fill (15 % of the outline color) is added so protected
        /// areas remain readable in 3D. Hide the rule to match the style exactly.
        /// </summary>
        public static List<OmtFillRule> ParkFills() => new List<OmtFillRule>
        {
            new OmtFillRule("", "", A("hsl(103, 36%, 66%)", 0.15f), 55, 8),
        };

        public static List<OmtLineRule> ParkOutlines() => new List<OmtLineRule>
        {
            new OmtLineRule("", "", C("hsl(107, 34%, 45%)"), 0f, 1f, 56, 8)
                .WithCasing(A("hsl(103, 36%, 66%)", 0.8f), 1.2f, 0f),
        };

        // ---- aeroway ----------------------------------------------------------

        public static List<OmtFillRule> AerowayFills()
        {
            Color runway = C("hsl(234, 25%, 76%)");
            return new List<OmtFillRule>
            {
                new OmtFillRule("aerodrome", "", C("hsl(53, 16%, 89%)"), 0, 11),
                new OmtFillRule("heliport", "", C("hsl(53, 16%, 89%)"), 0, 11),
                new OmtFillRule("apron", "", C("hsl(240, 8%, 88%)"), 1, 11),
                new OmtFillRule("runway", "", runway, 2, 11),
                new OmtFillRule("taxiway", "", runway, 2, 11),
                new OmtFillRule("helipad", "", runway, 2, 11),
                new OmtFillRule("", "", C("hsl(53, 16%, 89%)"), 0, 11),
            };
        }

        public static List<OmtLineRule> AerowayLines() => new List<OmtLineRule>
        {
            new OmtLineRule("runway", "", C("hsl(234, 25%, 76%)"), 45f, 3f, 57, 11),
            new OmtLineRule("taxiway", "", C("hsl(234, 25%, 76%)"), 20f, 1f, 58, 11),
        };

        // ---- waterway ---------------------------------------------------------

        public static Color WaterwayColor => C("hsl(210, 73%, 78%)");

        public static List<OmtLineRule> WaterwayLines()
        {
            Color c = WaterwayColor;
            return new List<OmtLineRule>
            {
                new OmtLineRule("river", "", c, 10f, 1.2f, 48),
                new OmtLineRule("canal", "", c, 8f, 1f, 50),
                new OmtLineRule("stream", "", c, 2.5f, 0.6f, 50),
                new OmtLineRule("drain", "", c, 1.5f, 0.5f, 50),
                new OmtLineRule("ditch", "", c, 1.5f, 0.5f, 50),
                new OmtLineRule("", "", c, 2f, 0.5f, 50),
            };
        }

        /// <summary>Casing color of waterway bridges (aqueducts) and their fill.</summary>
        public static Color WaterwayBridgeCasing => C("hsl(0, 0%, 73%)");
        public static Color WaterwayBridgeFill => C("hsl(204, 92%, 75%)");

        // ---- boundary ---------------------------------------------------------

        public static List<OmtLineRule> BoundaryLines() => new List<OmtLineRule>
        {
            new OmtLineRule("2", "", C("hsl(303, 17%, 56%)"), 0f, 2f, 204),
            new OmtLineRule("3", "", C("hsl(301, 23%, 42%)"), 0f, 1.2f, 203, 3).WithDash(5, 3),
            new OmtLineRule("4", "", C("hsl(301, 23%, 42%)"), 0f, 1.2f, 203, 3).WithDash(5, 3),
            new OmtLineRule("5", "", C("hsl(301, 15%, 58%)"), 0f, 0.9f, 202, 8).WithDash(4, 3),
            new OmtLineRule("6", "", C("hsl(301, 15%, 58%)"), 0f, 0.9f, 202, 8).WithDash(4, 3),
            new OmtLineRule("7", "", C("hsl(301, 10%, 66%)"), 0f, 0.7f, 201, 10) { Visible = false }.WithDash(3, 3),
            new OmtLineRule("8", "", C("hsl(301, 10%, 66%)"), 0f, 0.7f, 201, 10) { Visible = false }.WithDash(3, 3),
            new OmtLineRule("9", "", C("hsl(301, 10%, 70%)"), 0f, 0.6f, 200, 11) { Visible = false }.WithDash(2, 2),
            new OmtLineRule("10", "", C("hsl(301, 10%, 70%)"), 0f, 0.6f, 200, 11) { Visible = false }.WithDash(2, 2),
        };

        /// <summary>Outline of <c>class=aboriginal_lands</c> polygons (not styled in OSM OpenMapTiles).</summary>
        public static List<OmtLineRule> BoundaryAreaOutlines() => new List<OmtLineRule>
        {
            new OmtLineRule("aboriginal_lands", "", C("hsl(30, 45%, 55%)"), 0f, 1.2f, 205, 4).WithDash(3, 2),
        };

        public static Color BoundaryDisputed => C("hsl(246, 17%, 56%)");
        public static Color BoundaryMaritime => C("hsl(205, 45%, 65%)");

        // ---- transportation ---------------------------------------------------

        public static List<OmtLineRule> TransportationLines()
        {
            Color white = Color.white;
            Color pathCasing = A("hsl(0, 0%, 93%)", 0.6f);
            var rules = new List<OmtLineRule>
            {
                // Roads: fill + casing; order = draw order of the fills (minor roads below major ones).
                new OmtLineRule("motorway", "", C("hsl(349, 65%, 74%)"), 22f, 2.5f, 151, 4)
                    .WithCasing(C("hsl(339, 72%, 51%)")),
                new OmtLineRule("trunk", "", C("hsl(14, 89%, 79%)"), 18f, 2.2f, 148, 5)
                    .WithCasing(C("hsl(12, 62%, 48%)")),
                new OmtLineRule("primary", "", C("hsl(34, 94%, 82%)"), 16f, 2f, 145, 7)
                    .WithCasing(C("hsl(44, 100%, 31%)")),
                new OmtLineRule("secondary", "", C("hsl(63, 86%, 86%)"), 14f, 1.6f, 142, 8)
                    .WithCasing(C("hsl(67, 92%, 25%)")),
                new OmtLineRule("tertiary", "", white, 12f, 1.4f, 139, 9)
                    .WithCasing(C("hsl(0, 0%, 56%)")),
                new OmtLineRule("minor", "", white, 9f, 1f, 136, 12)
                    .WithCasing(C("hsl(0, 0%, 73%)")),
                new OmtLineRule("service", "", white, 5f, 0.6f, 130, 12)
                    .WithCasing(C("hsl(0, 0%, 73%)"), 0.5f, 0.5f),
                new OmtLineRule("busway", "", C("hsl(214, 60%, 88%)"), 7f, 0.8f, 131, 12)
                    .WithCasing(C("hsl(214, 30%, 65%)"), 0.5f, 0.5f),
                new OmtLineRule("bus_guideway", "", C("hsl(214, 30%, 60%)"), 3f, 0.6f, 131, 13),
                new OmtLineRule("raceway", "", C("hsl(351, 97%, 87%)"), 8f, 1f, 133, 9),
                new OmtLineRule("track", "", C("hsl(42, 61%, 41%)"), 3f, 1f, 131, 12)
                    .WithDash(3, 2.5f).WithCasing(A("hsl(0, 0%, 100%)", 0.5f), 0.5f, 0.5f),

                // Paths by subclass (bicycle/horse=designated are mapped to cycleway/bridleway by the handler).
                new OmtLineRule("path", "pedestrian", C("hsl(240, 19%, 89%)"), 6f, 1f, 116, 12)
                    .WithCasing(C("hsl(0, 0%, 60%)"), 0.5f, 0.5f),
                new OmtLineRule("path", "steps", C("hsl(6, 93%, 71%)"), 2f, 1.2f, 122, 12)
                    .WithDash(0.5f, 0.5f).WithCasing(pathCasing, 0.6f, 0.3f),
                new OmtLineRule("path", "cycleway", C("hsl(240, 100%, 50%)"), 1.5f, 0.7f, 121, 12)
                    .WithDash(2, 2).WithCasing(pathCasing, 0.6f, 0.3f),
                new OmtLineRule("path", "bridleway", C("hsl(120, 100%, 25%)"), 1.5f, 0.7f, 123, 12)
                    .WithDash(2, 2).WithCasing(pathCasing, 0.6f, 0.3f),
                new OmtLineRule("path", "platform", C("hsl(0, 0%, 73%)"), 3f, 0.8f, 100, 13),
                new OmtLineRule("path", "corridor", C("hsl(6, 60%, 80%)"), 1.5f, 0.6f, 124, 14).WithDash(2, 1),
                new OmtLineRule("path", "", C("hsl(6, 93%, 71%)"), 1.5f, 0.6f, 124, 12)
                    .WithDash(2, 1).WithCasing(pathCasing, 0.6f, 0.3f),

                // Railways: gray line with white hatching (major) or light hatching (tram/light rail).
                new OmtLineRule("transit", "subway", C("hsl(60, 1%, 50%)"), 2f, 0.8f, 152, 13).WithDash(1, 1.2f),
                new OmtLineRule("transit", "", C("hsl(0, 0%, 51%)"), 1.5f, 0.8f, 155, 12)
                    .WithOverlay(C("hsl(0, 0%, 73%)"), 1.6f, 0.2f, 4f),
                new OmtLineRule("rail", "", C("hsl(0, 0%, 51%)"), 3f, 1f, 154, 5)
                    .WithOverlay(Color.white, 0.5f, 2.5f, 2.5f),
                new OmtLineRule("aerialway", "", C("hsl(0, 0%, 50%)"), 1.5f, 1f, 201, 13)
                    .WithOverlay(C("hsl(0, 0%, 26%)"), 1.2f, 0.5f, 10f),
                new OmtLineRule("ferry", "", C("hsl(240, 90%, 73%)"), 2f, 0.8f, 234, 6).WithDash(2, 2),
                new OmtLineRule("pier", "", C("hsl(42, 49%, 93%)"), 3f, 1f, 98, 13),
            };

            // Roads under construction: the base color with white dashes, narrower than the finished road.
            void Construction(string cls, string fill, float width, int minZoom) =>
                rules.Add(new OmtLineRule(cls + "_construction", "", C(fill), width, 1f, 135, minZoom)
                    .WithOverlay(Color.white, 1f, 1f, 1f));
            Construction("motorway", "hsl(349, 65%, 74%)", 14f, 5);
            Construction("trunk", "hsl(14, 89%, 79%)", 12f, 7);
            Construction("primary", "hsl(34, 94%, 82%)", 11f, 7);
            Construction("secondary", "hsl(63, 86%, 86%)", 10f, 8);
            Construction("tertiary", "hsl(0, 0%, 73%)", 8f, 9);
            Construction("minor", "hsl(0, 0%, 83%)", 6f, 12);
            Construction("service", "hsl(0, 0%, 83%)", 4f, 12);
            Construction("track", "hsl(42, 61%, 60%)", 3f, 12);
            Construction("raceway", "hsl(351, 97%, 87%)", 6f, 12);
            Construction("path", "hsl(6, 93%, 80%)", 1.5f, 13);
            return rules;
        }

        public static List<OmtFillRule> TransportationAreas() => new List<OmtFillRule>
        {
            new OmtFillRule("pier", "", C("hsl(42, 49%, 93%)"), 97),
            new OmtFillRule("bridge", "", C("hsl(0, 0%, 72%)"), 99, 11),
            new OmtFillRule("path", "platform", C("hsl(0, 0%, 73%)"), 100),
            new OmtFillRule("", "", C("hsl(240, 19%, 89%)"), 101),
        };

        /// <summary>Casing color forced on bridges, so they read as raised decks.</summary>
        public static Color BridgeCasing => C("hsl(0, 0%, 35%)");

        // ---- mountain_peak lines ----------------------------------------------

        public static List<OmtLineRule> MountainLines() => new List<OmtLineRule>
        {
            new OmtLineRule("cliff", "", C("hsl(0, 0%, 45%)"), 0f, 1.5f, 1, 12),
            new OmtLineRule("ridge", "", C("hsl(29, 30%, 55%)"), 0f, 1f, 0, 12).WithDash(3, 2),
            new OmtLineRule("arete", "", C("hsl(29, 30%, 50%)"), 0f, 1f, 0, 12).WithDash(3, 2),
        };

        // ---- building ---------------------------------------------------------

        public static Color Building => C("hsl(26, 17%, 82%)");
    }
}
