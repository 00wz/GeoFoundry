using System.Collections.Generic;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Default style tables of the OpenMapTiles handlers, transcribed from the official "OSM OpenMapTiles" style
    /// (<c>layers/*/style.json</c> of the openmaptiles repository, CC-BY 4.0). Colors are copied verbatim.
    /// Semi-transparent style colors keep their alpha (the style's fill/line opacity): the draped shaders blend by it.
    /// Fill patterns of the style (forest leaves, wetland reeds, military hatching) are not reproduced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zooms.</b> A minimum zoom is never below the tile zoom from which the OpenMapTiles schema (3.16, the
    /// reference SQL of <c>layers/*/*.sql</c>) puts the class into the tiles ("schema: zN" below); otherwise it is the
    /// style's <c>minzoom</c>. Fill zooms are tile zooms; line zooms are display zooms, at which the line shader fades
    /// the line in on tiles of any zoom.
    /// </para>
    /// <para>
    /// <b>Widths.</b> The style gives screen pixels interpolated by zoom; a rule gives real meters and a minimum in
    /// pixels, <c>max(meters / metersPerPixel, pixels)</c> evaluated at the camera's zoom. Both are fitted so that
    /// the on-screen width follows the style over zooms 12–16 at Moscow's latitude (largest error ×1.2–1.6);
    /// below zoom 12 lines keep their minimum width and are thicker than in the style. Casings are the style's
    /// outline width minus the fill width, per side.
    /// </para>
    /// </remarks>
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

        /// <remarks>
        /// Subclass rules start at the style's zoom 9 (park 13); below it the class rules (the style's "medium scale"
        /// layers) cover them from the schema's zoom 7. Subclasses the style does not draw separately (flowerbed,
        /// shrubbery, fell, tundra, bog, wet_meadow, saltern) use their class color at all zooms.
        /// </remarks>
        public static List<OmtFillRule> LandcoverFills()
        {
            Color wood = C("hsl(102, 36%, 72%)"), grass = C("hsl(91, 60%, 81%)"), sand = C("hsl(45, 70%, 87%)");
            Color rock = C("hsl(30, 35%, 90%)"), farmland = C("hsl(64, 47%, 89%)"), scrub = C("hsl(80, 35%, 76%)");
            Color orchard = C("hsl(109, 48%, 76%)"), heath = C("hsl(63, 43%, 74%)");
            return new List<OmtFillRule>
            {
                // Subclass-specific (style layers 19–43).
                new OmtFillRule("", "park", C("hsl(125, 83%, 88%)"), 20, 13),
                new OmtFillRule("", "garden", grass, 23, 9),
                new OmtFillRule("", "wood", wood, 25, 9),
                new OmtFillRule("", "forest", wood, 35, 9),
                new OmtFillRule("", "tidalflat", C("hsl(28, 19%, 84%)"), 26, 9),
                new OmtFillRule("", "wetland", wood, 27, 9),
                new OmtFillRule("", "swamp", wood, 27, 9),
                // The style's marsh is bright yellow under its reed pattern; without the pattern, the wetland color.
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
                new OmtFillRule("", "reedbed", grass, 32, 9),
                new OmtFillRule("", "saltmarsh", grass, 32, 9),
                new OmtFillRule("", "village_green", grass, 32, 9),
                new OmtFillRule("", "mangrove", scrub, 33, 9),
                new OmtFillRule("", "scrub", scrub, 38, 9),
                new OmtFillRule("", "heath", heath, 34, 9),
                new OmtFillRule("", "farmland", farmland, 36, 9),
                new OmtFillRule("", "farm", C("hsl(35, 75%, 85%)"), 37, 9),
                new OmtFillRule("", "allotments", C("hsl(102, 36%, 82%)"), 42, 9),
                new OmtFillRule("", "grass", grass, 43, 9),
                new OmtFillRule("", "grassland", grass, 43, 9),
                new OmtFillRule("", "golf_course", grass, 43, 9),
                // Class rules (style layers 13–18 and 45); schema: z7 (ice from Natural Earth at all zooms).
                new OmtFillRule("ice", "", C("hsl(180, 28%, 90%)"), 45, 5),
                new OmtFillRule("wetland", "", wood, 13, 7),
                new OmtFillRule("sand", "", sand, 14, 7),
                new OmtFillRule("grass", "", grass, 15, 7),
                new OmtFillRule("rock", "", rock, 16, 7),
                new OmtFillRule("wood", "", wood, 17, 7),
                new OmtFillRule("farmland", "", farmland, 18, 7),
            };
        }

        public static List<OmtLineRule> LandcoverOutlines() => new List<OmtLineRule>
        {
            new OmtLineRule("", "allotments", C("hsl(102, 21%, 72%)"), 0f, 0.5f, 44, 15),
            new OmtLineRule("", "farm", C("hsl(35, 43%, 68%)"), 0f, 0.5f, 44, 15),
            new OmtLineRule("", "farmland", C("hsl(64, 20%, 74%)"), 0f, 0.5f, 44, 15),
            new OmtLineRule("", "recreation_ground", C("hsl(126, 26%, 32%)"), 0f, 0.3f, 44, 15),
            new OmtLineRule("ice", "", C("hsl(210, 100%, 80%)"), 0f, 1.5f, 46, 5).WithDash(4, 2),
        };

        // ---- landuse ----------------------------------------------------------

        /// <remarks>
        /// Schema: residential from z4 (Natural Earth), suburb, quarter and neighbourhood from z6, all other classes
        /// from z9. The style's residential filter lists "suburbs" (a typo), so it never fills suburb and quarter;
        /// they are filled here as intended. Library, bus station and parking are not styled; they are extensions.
        /// </remarks>
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
                new OmtFillRule("cemetery", "", C("hsl(129, 24%, 73%)"), 2, 9),
                new OmtFillRule("military", "", C("hsl(0, 75%, 95%)"), 3, 9),
                new OmtFillRule("railway", "", industrial, 4, 9),
                new OmtFillRule("garages", "", C("hsl(53, 21%, 84%)"), 5, 9),
                new OmtFillRule("dam", "", C("hsl(0, 0%, 68%)"), 6, 9),
                new OmtFillRule("quarry", "", C("hsl(0, 0%, 83%)"), 7, 9),
                new OmtFillRule("industrial", "", industrial, 8, 9),
                new OmtFillRule("retail", "", C("hsl(7, 100%, 91%)"), 9, 9),
                new OmtFillRule("commercial", "", C("hsl(2, 49%, 90%)"), 10, 9),
                new OmtFillRule("bus_station", "", C("hsl(0, 0%, 88%)"), 10, 12),
                new OmtFillRule("parking", "", C("hsl(0, 0%, 93%)"), 10, 13),
                new OmtFillRule("school", "", education, 11, 9),
                new OmtFillRule("university", "", education, 11, 9),
                new OmtFillRule("kindergarten", "", education, 11, 9),
                new OmtFillRule("college", "", education, 11, 9),
                new OmtFillRule("hospital", "", education, 11, 9),
                new OmtFillRule("library", "", education, 11, 9),
                new OmtFillRule("stadium", "", stadium, 21, 9),
                new OmtFillRule("playground", "", stadium, 21, 9),
                new OmtFillRule("pitch", "", pitch, 22, 9),
                new OmtFillRule("track", "", pitch, 22, 9),
                // Outlined only in the style.
                new OmtFillRule("theme_park", "", C("hsl(0, 0%, 0%)"), 0, 9) { Visible = false },
                new OmtFillRule("zoo", "", C("hsl(0, 0%, 0%)"), 0, 9) { Visible = false },
            };
        }

        public static List<OmtLineRule> LanduseOutlines()
        {
            Color edu = C("hsl(60, 13%, 60%)");
            // The style's "Theme park" band (3.5–5.5 px, 30 % opacity, offset inward) with the 1 px outline on top.
            Color themePark = C("hsl(330, 100%, 20%)");
            return new List<OmtLineRule>
            {
                new OmtLineRule("railway", "", C("hsl(309, 14%, 74%)"), 0f, 0.7f, 60, 13),
                new OmtLineRule("military", "", A("hsl(0, 100%, 67%)", 0.24f), 0f, 2f, 60, 13),
                new OmtLineRule("residential", "", C("hsl(0, 0%, 73%)"), 0f, 0.5f, 60, 13),
                new OmtLineRule("commercial", "", C("hsl(2, 49%, 90%)"), 0f, 0.5f, 60, 13),
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
                new OmtLineRule("theme_park", "", A("hsl(330, 100%, 20%)", 0.3f), 0f, 5.5f, 61, 13)
                    .WithOverlay(themePark, 0.18f),
                new OmtLineRule("zoo", "", A("hsl(330, 100%, 20%)", 0.3f), 0f, 5.5f, 61, 13)
                    .WithOverlay(themePark, 0.18f),
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

        /// <summary>
        /// The style's "National parks" band (3.6 px, 80 % opacity) with the "National park outline" (1.5 px) on top;
        /// the outline is drawn with the band from zoom 8 (the style adds it at 14), and the band is centered on the
        /// boundary (the style offsets it inward).
        /// </summary>
        public static List<OmtLineRule> ParkOutlines() => new List<OmtLineRule>
        {
            new OmtLineRule("", "", A("hsl(103, 36%, 66%)", 0.8f), 0f, 3.6f, 55, 8)
                .WithOverlay(C("hsl(107, 34%, 45%)"), 0.42f),
        };

        // ---- aeroway ----------------------------------------------------------

        /// <summary>The style fills every aeroway polygon with one color (schema: z10, style: z11).</summary>
        public static List<OmtFillRule> AerowayFills() => new List<OmtFillRule>
        {
            new OmtFillRule("", "", C("hsl(53, 16%, 89%)"), 12, 11),
        };

        public static List<OmtLineRule> AerowayLines() => new List<OmtLineRule>
        {
            new OmtLineRule("runway", "", C("hsl(234, 25%, 76%)"), 18.5f, 7.5f, 57, 11),
            new OmtLineRule("taxiway", "", C("hsl(234, 25%, 76%)"), 9f, 3.1f, 58, 11),
        };

        // ---- waterway ---------------------------------------------------------

        public static Color WaterwayColor => C("hsl(210, 73%, 78%)");

        /// <remarks>
        /// Schema: rivers from z3 (Natural Earth), canals from z12, all other classes from z13. The style sizes
        /// rivers ("River") and all other classes ("Other waterway") with two width functions.
        /// </remarks>
        public static List<OmtLineRule> WaterwayLines()
        {
            Color c = WaterwayColor;
            return new List<OmtLineRule>
            {
                new OmtLineRule("river", "", c, 7.5f, 2.1f, 48, 3),
                new OmtLineRule("canal", "", c, 1.5f, 0.6f, 50, 12),
                new OmtLineRule("stream", "", c, 1.5f, 0.6f, 50, 13),
                new OmtLineRule("drain", "", c, 1.5f, 0.6f, 50, 13),
                new OmtLineRule("ditch", "", c, 1.5f, 0.6f, 50, 13),
                new OmtLineRule("", "", c, 1.5f, 0.6f, 50, 13),
            };
        }

        /// <summary>Display zoom from which waterway tunnels are drawn ("River tunnel").</summary>
        public const float WaterwayTunnelMinZoom = 14f;

        /// <summary>Casing color of waterway bridges (aqueducts) and their fill.</summary>
        public static Color WaterwayBridgeCasing => C("hsl(0, 0%, 73%)");
        public static Color WaterwayBridgeFill => C("hsl(204, 92%, 75%)");

        /// <summary>Width of waterway bridges of every class ("River bridge"), meters and minimum pixels.</summary>
        public const float WaterwayBridgeMeters = 2f, WaterwayBridgePixels = 0.7f;

        /// <summary>Casing of waterway bridges per side ("River bridge outline"), meters and minimum pixels.</summary>
        public const float WaterwayBridgeCasingMeters = 1.5f, WaterwayBridgeCasingPixels = 0.7f;

        // ---- boundary ---------------------------------------------------------

        /// <remarks>
        /// Schema: admin_level 2 from z0 and 4 from z1 (Natural Earth), 3 from z5, 5–6 from z10, 7–8 from z12, 9–10
        /// from z13. The style draws only levels 2–4 ("Country border", "Other border"); the others are hidden rules.
        /// </remarks>
        public static List<OmtLineRule> BoundaryLines()
        {
            Color other = C("hsl(301, 23%, 42%)"), local = C("hsl(301, 15%, 58%)");
            return new List<OmtLineRule>
            {
                new OmtLineRule("2", "", C("hsl(303, 17%, 56%)"), 0f, 3f, 204),
                new OmtLineRule("3", "", other, 0f, 1.6f, 203, 5).WithDash(5, 3),
                new OmtLineRule("4", "", other, 0f, 1.6f, 203, 3).WithDash(5, 3),
                new OmtLineRule("5", "", local, 0f, 0.9f, 202, 10) { Visible = false }.WithDash(4, 3),
                new OmtLineRule("6", "", local, 0f, 0.9f, 202, 10) { Visible = false }.WithDash(4, 3),
                new OmtLineRule("7", "", local, 0f, 0.7f, 201, 12) { Visible = false }.WithDash(3, 3),
                new OmtLineRule("8", "", local, 0f, 0.7f, 201, 12) { Visible = false }.WithDash(3, 3),
                new OmtLineRule("9", "", local, 0f, 0.6f, 200, 13) { Visible = false }.WithDash(2, 2),
                new OmtLineRule("10", "", local, 0f, 0.6f, 200, 13) { Visible = false }.WithDash(2, 2),
            };
        }

        /// <summary>Outline of <c>class=aboriginal_lands</c> polygons (not styled in OSM OpenMapTiles).</summary>
        public static List<OmtLineRule> BoundaryAreaOutlines() => new List<OmtLineRule>
        {
            new OmtLineRule("aboriginal_lands", "", C("hsl(30, 45%, 55%)"), 0f, 1.2f, 205, 4).WithDash(3, 2),
        };

        public static Color BoundaryDisputed => C("hsl(246, 17%, 56%)");

        // ---- transportation ---------------------------------------------------

        /// <remarks>
        /// Schema: motorway and trunk from z4, primary z8, secondary z9, tertiary z11, minor, raceway, track and paths
        /// z12, service z13; links z9; rail z8, light rail z11, subway, tram and monorail z14, aerialway z12, ferry z4,
        /// areas z13. Construction classes follow their road, except minor and service (z13), track and path (z14).
        /// </remarks>
        public static List<OmtLineRule> TransportationLines()
        {
            Color white = Color.white;
            Color pathCasing = A("hsl(0, 0%, 93%)", 0.6f);
            Color transit = C("hsl(0, 0%, 51%)"), transitHatching = C("hsl(0, 0%, 73%)");
            var rules = new List<OmtLineRule>
            {
                // Roads: fill + casing; order = draw order of the fills (minor roads below major ones).
                new OmtLineRule("motorway", "", C("hsl(349, 65%, 74%)"), 15f, 8.8f, 151, 5)
                    .WithCasing(C("hsl(339, 72%, 51%)"), 0.6f, 0.5f),
                new OmtLineRule("trunk", "", C("hsl(14, 89%, 79%)"), 12.5f, 4.9f, 148, 5)
                    .WithCasing(C("hsl(12, 62%, 48%)"), 0.7f, 0.5f).WithBridgeCasing(C("hsl(12, 62%, 48%)")),
                new OmtLineRule("primary", "", C("hsl(34, 94%, 82%)"), 12.5f, 4.9f, 145, 8)
                    .WithCasing(C("hsl(44, 100%, 31%)"), 0.7f, 0.5f),
                new OmtLineRule("secondary", "", C("hsl(63, 86%, 86%)"), 13.5f, 5.7f, 142, 9)
                    .WithCasing(C("hsl(67, 92%, 25%)"), 0.5f, 1f),
                new OmtLineRule("tertiary", "", white, 15.5f, 4.1f, 139, 11)
                    .WithCasing(C("hsl(0, 0%, 56%)"), 0.6f, 0.5f),
                new OmtLineRule("minor", "", white, 8.5f, 0f, 136, 12)
                    .WithCasing(C("hsl(0, 0%, 73%)"), 0.7f, 0f).WithCasingMinZoom(13),
                new OmtLineRule("service", "", white, 3.5f, 1.1f, 130, 13)
                    .WithCasing(C("hsl(0, 0%, 73%)"), 0.6f, 0.5f).WithCasingMinZoom(13)
                    .WithBridgeCasing(C("hsl(36, 5%, 80%)")),
                // Busways are not styled: a light blue service road.
                new OmtLineRule("busway", "", C("hsl(214, 60%, 88%)"), 3.5f, 1.1f, 131, 12)
                    .WithCasing(C("hsl(214, 30%, 65%)"), 0.6f, 0.5f),
                new OmtLineRule("bus_guideway", "", C("hsl(214, 30%, 60%)"), 3f, 0.6f, 131, 13),
                new OmtLineRule("raceway", "", C("hsl(351, 97%, 87%)"), 10f, 2.9f, 133, 12),
                new OmtLineRule("track", "", C("hsl(42, 61%, 41%)"), 0f, 1.7f, 131, 12)
                    .WithDash(3, 2.5f).WithCasing(A("hsl(0, 0%, 100%)", 0.5f), 0.3f, 0.5f).WithCasingMinZoom(13),

                // Paths by subclass (bicycle/horse=designated are mapped to cycleway/bridleway by the handler).
                new OmtLineRule("path", "pedestrian", C("hsl(240, 19%, 89%)"), 8.5f, 1.5f, 116, 12)
                    .WithCasing(C("hsl(0, 0%, 60%)"), 0.6f, 0.5f),
                new OmtLineRule("path", "steps", C("hsl(6, 93%, 71%)"), 0f, 1.8f, 122, 12)
                    .WithDash(0.5f, 0.5f).WithCasing(pathCasing, 1.1f, 0f).WithCasingMinZoom(15),
                new OmtLineRule("path", "cycleway", C("hsl(240, 100%, 50%)"), 0f, 0.8f, 121, 12)
                    .WithDash(2, 2).WithCasing(pathCasing, 1.1f, 0f).WithCasingMinZoom(15),
                new OmtLineRule("path", "bridleway", C("hsl(120, 100%, 25%)"), 0f, 0.8f, 123, 12)
                    .WithDash(2, 2).WithCasing(pathCasing, 1.1f, 0f).WithCasingMinZoom(15),
                // Platform and corridor lines are not styled; they are extensions.
                new OmtLineRule("path", "platform", C("hsl(0, 0%, 73%)"), 3f, 0.8f, 100, 13),
                new OmtLineRule("path", "corridor", C("hsl(6, 60%, 80%)"), 0f, 0.9f, 124, 14).WithDash(2, 1),
                new OmtLineRule("path", "", C("hsl(6, 93%, 71%)"), 0f, 0.9f, 124, 12)
                    .WithDash(2, 1).WithCasing(pathCasing, 0.9f, 0f).WithCasingMinZoom(15),

                // Railways: gray line with white hatching (major) or light hatching (tram/light rail).
                new OmtLineRule("transit", "subway", C("hsl(60, 1%, 50%)"), 1.5f, 1.2f, 152, 14).WithDash(1, 1.2f),
                new OmtLineRule("transit", "tram", transit, 1f, 1f, 155, 14)
                    .WithOverlay(transitHatching, 1.6f, 0.32f, 6.4f),
                new OmtLineRule("transit", "monorail", transit, 1f, 1f, 155, 14)
                    .WithOverlay(transitHatching, 1.6f, 0.32f, 6.4f),
                new OmtLineRule("transit", "", transit, 1f, 1f, 155, 12)
                    .WithOverlay(transitHatching, 1.6f, 0.32f, 6.4f),
                new OmtLineRule("rail", "", C("hsl(0, 0%, 51%)"), 2.5f, 2.3f, 154, 8)
                    .WithOverlay(Color.white, 0.6f, 1.5f, 1.5f),
                new OmtLineRule("aerialway", "", C("hsl(0, 0%, 50%)"), 1.5f, 1.5f, 201, 13)
                    .WithOverlay(C("hsl(0, 0%, 26%)"), 1.1f, 0.55f, 11f),
                new OmtLineRule("ferry", "", C("hsl(240, 90%, 73%)"), 0f, 0.9f, 234, 6).WithDash(2, 2),
                new OmtLineRule("pier", "", C("hsl(42, 49%, 93%)"), 1.5f, 0.9f, 98, 13),
            };

            // Roads under construction: the base color with white dashes, one width for all classes as in the style.
            void Construction(string cls, string fill, float meters, float pixels, int minZoom) =>
                rules.Add(new OmtLineRule(cls + "_construction", "", C(fill), meters, pixels, 135, minZoom)
                    .WithOverlay(Color.white, 1f, 1f, 1f));
            Construction("motorway", "hsl(349, 65%, 74%)", 7.5f, 3.1f, 5);
            Construction("trunk", "hsl(14, 89%, 79%)", 7.5f, 3.1f, 7);
            Construction("primary", "hsl(34, 94%, 82%)", 7.5f, 3.1f, 8);
            Construction("secondary", "hsl(63, 86%, 86%)", 7.5f, 3.1f, 9);
            Construction("tertiary", "hsl(0, 0%, 73%)", 7.5f, 3.1f, 11);
            Construction("minor", "hsl(0, 0%, 83%)", 7.5f, 3.1f, 13);
            // Service under construction: white dashes of the service road width.
            rules.Add(new OmtLineRule("service_construction", "", white, 3.5f, 1.1f, 132, 13).WithDash(1, 1));
            // Not styled: track, raceway and path under construction.
            Construction("track", "hsl(42, 61%, 60%)", 0f, 1.5f, 14);
            Construction("raceway", "hsl(351, 97%, 87%)", 7.5f, 3.1f, 12);
            Construction("path", "hsl(6, 93%, 80%)", 0f, 0.9f, 14);
            return rules;
        }

        public static List<OmtFillRule> TransportationAreas() => new List<OmtFillRule>
        {
            new OmtFillRule("pier", "", C("hsl(42, 49%, 93%)"), 97, 13),
            new OmtFillRule("bridge", "", C("hsl(0, 0%, 72%)"), 99, 13),
            new OmtFillRule("path", "platform", C("hsl(0, 0%, 73%)"), 100, 13),
            new OmtFillRule("", "", C("hsl(240, 19%, 89%)"), 101, 13),
        };

        /// <summary>Casing color of bridges whose rule sets no bridge casing (the style's black bridge outlines).</summary>
        public static Color BridgeCasing => C("hsl(0, 0%, 0%)");

        // ---- mountain_peak lines ----------------------------------------------

        /// <summary>Ridges, aretes and cliffs (schema: z13); not styled in OSM OpenMapTiles.</summary>
        public static List<OmtLineRule> MountainLines() => new List<OmtLineRule>
        {
            new OmtLineRule("cliff", "", C("hsl(0, 0%, 45%)"), 0f, 1.5f, 1, 13),
            new OmtLineRule("ridge", "", C("hsl(29, 30%, 55%)"), 0f, 1f, 0, 13).WithDash(3, 2),
            new OmtLineRule("arete", "", C("hsl(29, 30%, 50%)"), 0f, 1f, 0, 13).WithDash(3, 2),
        };

        // ---- building ---------------------------------------------------------

        public static Color Building => C("hsl(26, 17%, 82%)");
    }
}
