using System.Collections.Generic;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Default label styles of the OpenMapTiles handlers, transcribed from the symbol layers of the "OSM
    /// OpenMapTiles" style (CC-BY 4.0): text colors, halos, sizes, font faces, icons, zoom ranges and filters.
    /// Classes the style does not label get neutral defaults (marked "not styled").
    /// </summary>
    /// <remarks>
    /// <para>
    /// A minimum zoom is never below the tile zoom from which the OpenMapTiles schema (3.16, the reference SQL of
    /// <c>layers/*/*.sql</c>) puts the class into the tiles (marked "schema: zN" below). A label promised earlier than
    /// its data could not fade in with the zoom: it would pop up at full opacity when the tile that carries it loads.
    /// </para>
    /// <para>
    /// Sizes are the style's text size at zoom 14, or at the zoom nearest to 14 where the style layer is visible.
    /// Font faces map the style's Open Sans stacks to the three faces of <see cref="OmtTextSettings"/>: Semibold and
    /// Bold to Bold, Italic and Semibold Italic to Italic. Priorities follow the style's layer order within a
    /// handler: MapLibre places the upper layer first, so it gets the lower priority.
    /// </para>
    /// </remarks>
    public static class OmtLabelDefaults
    {
        private static Color C(string css) => OmtColor.Parse(css);

        private static readonly Color Halo = new Color(1f, 1f, 1f, 0.8f);

        private static OmtLabelRule R(string cls, string sub, float size, string color, float minZoom, int priority = 0) =>
            new OmtLabelRule(cls, sub, size, C(color), minZoom, priority).WithHalo(Halo, 1f);

        // ---- place ------------------------------------------------------------

        /// <summary>
        /// Place rules; matched by class and capital (<c>capital</c>, 2–6, is the subclass key of the handler). The
        /// style's "Capital city labels" match capital 2 (its filter's 1 is not a schema value); regional capitals are
        /// cities.
        /// </summary>
        public static List<OmtLabelRule> Place()
        {
            var other = C("hsl(0, 0%, 47%)");
            OmtLabelRule O(string cls, float minZoom, int priority) =>
                new OmtLabelRule(cls, "", 14, other, minZoom, priority).WithHalo(Color.white, 1.5f)
                    .WithFont(OmtFontStyle.Bold).WithWrap(10);
            return new List<OmtLabelRule>
            {
                // Not styled; schema: z0–3.
                R("continent", "", 14, "hsl(0, 0%, 20%)", 0, 0).WithFont(OmtFontStyle.Bold, OmtTextTransform.Uppercase).WithMaxZoom(4),
                R("country", "", 17, "hsl(273, 33%, 22%)", 2, 1).WithHalo(Halo, 0.8f).WithFont(OmtFontStyle.Bold)
                    .WithWrap(6.25f).WithMaxZoom(12),
                R("city", "2", 18, "hsl(0, 0%, 13%)", 3, 2).WithHalo(Halo, 1.2f).WithFont(OmtFontStyle.Bold)
                    .WithIcon("place-capital-8", OmtTextAnchor.Bottom).WithMaxZoom(15),
                R("city", "", 14, "hsl(0, 0%, 15%)", 4, 3).WithFont(OmtFontStyle.Bold)
                    .WithIcon("", OmtTextAnchor.Bottom).WithMaxZoom(14),
                R("state", "", 14, "hsl(302, 18%, 42%)", 4, 4).WithHalo(new Color(1, 1, 1, 0.7f), 0.8f)
                    .WithMaxZoom(12).WithMaxRank(2),
                // Not styled: as states.
                R("province", "", 14, "hsl(302, 18%, 42%)", 4, 4).WithHalo(new Color(1, 1, 1, 0.7f), 0.8f)
                    .WithMaxZoom(12).WithMaxRank(2),
                R("town", "", 13, "hsl(0, 0%, 20%)", 7, 5).WithHalo(Halo, 1.2f).WithFont(OmtFontStyle.Bold)
                    .WithIcon("", OmtTextAnchor.Bottom), // schema: z7
                R("village", "", 14, "hsl(0, 0%, 20%)", 8, 6).WithHalo(Halo, 1.2f).WithFont(OmtFontStyle.Bold), // schema: z8
                // "Other labels".
                O("suburb", 11, 7), // schema: z11
                O("hamlet", 11, 7), // schema: z11
                O("neighbourhood", 14, 7), // schema: z14
                O("island", 8, 7), // schema: z8
                // Not styled: as "Other labels".
                O("borough", 11, 8), // schema: z11
                O("quarter", 14, 8), // schema: z14
                O("isolated_dwelling", 14, 8), // schema: z14
                O("aboriginal_lands", 8, 8).WithFont(OmtFontStyle.Italic), // schema: z6
                O("", 14, 8),
            };
        }

        // ---- poi --------------------------------------------------------------

        /// <summary>POI rules; icons are named after the subclass, falling back to the class.</summary>
        /// <remarks>
        /// Schema: z12–13 carry only railway stations, halts, ferry terminals and large universities; the rest from z14.
        /// Rules are matched in this order, so the style's subclass filters come first (books, car_parts and
        /// supermarket are shops; art is not labeled); cinema and library use "Cultural" (z15) before "Public" (z16).
        /// </remarks>
        public static List<OmtLabelRule> Poi()
        {
            var rules = new List<OmtLabelRule>();

            OmtLabelRule P(string cls, string sub, float size, string color, float minZoom, int priority,
                OmtFontStyle font, string icon, float wrap = 9f, float halo = 1f) =>
                R(cls, sub, size, color, minZoom, priority).WithHalo(Color.white, halo).WithFont(font)
                    .WithIcon(icon).WithWrap(wrap);

            void Group(float size, string color, float minZoom, int priority, OmtFontStyle font, string icon,
                float halo, params string[] classes)
            {
                foreach (var cls in classes) rules.Add(P(cls, "", size, color, minZoom, priority, font, icon, 9f, halo));
            }

            const OmtFontStyle bold = OmtFontStyle.Bold, italic = OmtFontStyle.Italic, regular = OmtFontStyle.Regular;
            string shop = "hsl(300, 50%, 40%)", brown = "hsl(37, 87%, 24%)", green = "hsl(126, 100%, 27%)";
            string blue = "hsl(216, 100%, 50%)", train = "hsl(232, 41%, 48%)";

            // "Train": stations and halts from z12, tram stops and subway stations from z14 (schema).
            rules.Add(P("railway", "tram_stop", 11, train, 14, 2, bold, "square_train", 14f, 0.8f));
            rules.Add(P("railway", "subway", 11, train, 14, 2, bold, "square_train", 14f, 0.8f));
            rules.Add(P("railway", "", 11, train, 12, 2, bold, "square_train", 14f, 0.8f));
            rules.Add(P("zoo", "zoo", 12, "hsl(330, 100%, 20%)", 14, 0, bold, "{subclass}", 6f)); // style z12, schema z14
            rules.Add(P("shop", "mall", 12, "hsl(7, 100%, 41%)", 15, 3, italic, ""));
            rules.Add(P("park", "park", 10, "hsl(125, 83%, 28%)", 15, 1, italic, "", 6.5f));
            rules.Add(P("bus", "bus_stop", 10.4f, blue, 17, 6, regular, "bus_stop.12", 14f, 0.8f));
            rules.Add(P("bus", "", 11, blue, 17, 5, bold, "{subclass}", 14f, 0.8f));
            rules.Add(P("ferry_terminal", "ferry_terminal", 10, "hsl(261, 46%, 43%)", 15, 16, regular, "ferry"));
            // "Shop": text and icon only from z17, supermarkets from z15.
            rules.Add(P("", "supermarket", 10, shop, 15, 22, bold, "{subclass}"));
            rules.Add(P("", "books", 10, shop, 17, 22, bold, "{subclass}"));
            rules.Add(P("", "car_parts", 10, shop, 17, 22, bold, "{subclass}"));
            // The style also lists "seafood", which is not a schema class.
            Group(10, shop, 17, 22, bold, "{subclass}", 1f,
                "alcohol_shop", "bakery", "bicycle", "butcher", "clothing_store", "grocery", "hairdresser", "music",
                "shop");
            Group(10.6f, brown, 18, 21, bold, "{subclass}", 1f, "recycling", "waste_basket", "drinking_water", "toilets");
            rules.Add(P("cemetery", "", 11, "hsl(129, 24%, 23%)", 14, 20, italic, "", 14f, 0.8f));
            rules.Add(P("college", "", 11, "hsl(60, 100%, 15%)", 16, 19, italic, "", 9f, 0.8f));
            rules.Add(P("school", "", 11, "hsl(60, 100%, 15%)", 16, 19, italic, "", 9f, 0.8f));
            Group(10.2f, green, 16, 18, bold, "{subclass}", 1f, "pitch", "playground", "stile", "garden", "gate");
            Group(10.2f, green, 16, 17, italic, "{subclass}", 0.2f, "golf", "stadium", "swimming_pool", "sports_centre", "water_park");
            Group(10.2f, "hsl(35, 100%, 39%)", 16, 15, bold, "{subclass}", 1f,
                "ice_cream", "cafe", "beer", "bar", "fast_food", "restaurant");
            // "Cultural" excludes the subclass art of art_gallery; the style's classes gallery and arts_centre are
            // subclasses of art_gallery in the schema.
            var art = P("art_gallery", "art", 10, brown, 15, 13, bold, "");
            art.Visible = false;
            rules.Add(art);
            Group(10, brown, 15, 13, bold, "{subclass}", 1f, "cinema", "library", "museum", "castle", "monument", "art_gallery");
            Group(10.2f, brown, 16, 14, bold, "{subclass}", 1f,
                "atm", "town_hall", "post", "police", "information", "theatre", "fire_station");
            rules.Add(P("attraction", "", 10, "hsl(330, 100%, 20%)", 15, 12, bold, ""));
            Group(10.4f, blue, 17, 11, bold, "{subclass}", 1f, "bicycle_parking", "car", "fuel", "parking");
            // "Health": hospitals and pharmacies from z16, the rest from z17.
            Group(10.3f, "hsl(0, 100%, 37%)", 16, 10, regular, "{class}", 1f, "hospital", "pharmacy");
            Group(10.3f, "hsl(0, 100%, 37%)", 17, 10, regular, "{class}", 1f, "dentist", "veterinary");
            rules.Add(P("campsite", "", 10.2f, blue, 16, 9, bold, "camping"));
            rules.Add(P("lodging", "", 10.4f, blue, 17, 8, bold, "{subclass}"));
            rules.Add(P("place_of_worship", "", 10.4f, "hsl(240, 12%, 25%)", 16, 7, bold, "{subclass}"));
            rules.Add(P("harbor", "", 14, "hsl(230, 68%, 61%)", 16, 4, italic, "", 6f, 0.3f));
            // Not styled.
            rules.Add(P("", "", 10, "hsl(0, 0%, 35%)", 16, 23, regular, "{subclass}"));
            return rules;
        }

        // ---- housenumber ------------------------------------------------------

        public static List<OmtLabelRule> Housenumber() => new List<OmtLabelRule>
        {
            R("", "", 9, "hsl(0, 0%, 40%)", 17, 10).WithWrap(0), // schema: z14
        };

        // ---- mountain_peak ----------------------------------------------------

        /// <remarks>
        /// Points (schema: z7) are labeled from z12, where the style's text appears (it shows only the icon before),
        /// up to z16. Ridge, arete and cliff lines (schema: z13) are not styled; they are labeled along the lines.
        /// </remarks>
        public static List<OmtLabelRule> MountainPeak() => new List<OmtLabelRule>
        {
            R("peak", "", 10, "hsl(29, 57%, 27%)", 12, 0).WithIcon("peak").WithWrap(6).WithMaxZoom(16),
            R("saddle", "", 10, "hsl(29, 57%, 27%)", 12, 0).WithIcon("peak").WithWrap(6).WithMaxZoom(16),
            R("volcano", "", 10, "hsl(0, 100%, 42%)", 12, 1).WithIcon("volcano").WithWrap(6).WithMaxZoom(16),
            R("ridge", "", 10, "hsl(29, 40%, 35%)", 13, 2).WithFont(OmtFontStyle.Italic),
            R("arete", "", 10, "hsl(29, 40%, 35%)", 13, 2).WithFont(OmtFontStyle.Italic),
            R("cliff", "", 10, "hsl(0, 0%, 30%)", 14, 2).WithFont(OmtFontStyle.Italic),
            R("", "", 10, "hsl(29, 57%, 27%)", 12, 0).WithIcon("peak").WithWrap(6).WithMaxZoom(16),
        };

        // ---- aerodrome_label --------------------------------------------------

        /// <remarks>
        /// "Major airport labels": airports with an IATA code (the handler's filter), name from z11 (the style shows
        /// only the icon from z8), up to z17. Classes only order the placement.
        /// </remarks>
        public static List<OmtLabelRule> AerodromeLabel()
        {
            var rules = new List<OmtLabelRule>();
            string[] classes = { "international", "public", "regional", "military", "private", "other" };
            for (int i = 0; i < classes.Length; i++)
            {
                rules.Add(R(classes[i], "", 12, "hsl(261, 46%, 43%)", 11, i)
                    .WithFont(OmtFontStyle.Italic).WithIcon("aerodrome.12").WithWrap(9).WithMaxZoom(17));
            }
            rules.Add(R("", "", 12, "hsl(261, 46%, 43%)", 11, 6).WithFont(OmtFontStyle.Italic).WithIcon("aerodrome.12")
                .WithWrap(9).WithMaxZoom(17));
            return rules;
        }

        // ---- water_name -------------------------------------------------------

        /// <remarks>
        /// "Water labels" show every point but oceans from z15. Oceans, seas and straits (marine points) keep
        /// earlier, not styled zooms, since the style shows no open-sea names below z15.
        /// </remarks>
        public static List<OmtLabelRule> WaterName() => new List<OmtLabelRule>
        {
            R("ocean", "", 16, "hsl(210, 39%, 45%)", 0, 0).WithFont(OmtFontStyle.Italic).WithHalo(new Color(1, 1, 1, 0.6f), 1f).WithWrap(6),
            R("sea", "", 13, "hsl(210, 39%, 45%)", 3, 0).WithFont(OmtFontStyle.Italic).WithHalo(new Color(1, 1, 1, 0.6f), 1f).WithWrap(6),
            R("strait", "", 11, "hsl(210, 39%, 49%)", 8, 0).WithFont(OmtFontStyle.Italic).WithWrap(5),
            R("bay", "", 11, "hsl(210, 39%, 49%)", 15, 0).WithWrap(5), // schema: z13
            R("lake", "", 11, "hsl(210, 39%, 49%)", 15, 0).WithWrap(5),
            R("", "", 11, "hsl(210, 39%, 49%)", 15, 0).WithWrap(5),
        };

        /// <summary>Labels along lake center lines ("Lakeline labels").</summary>
        public static List<OmtLabelRule> WaterNameLines() => new List<OmtLabelRule>
        {
            R("", "", 12, "hsl(238, 43%, 55%)", 3, 1).WithHalo(new Color(1, 1, 1, 0.7f), 1f).WithWrap(5), // schema: z3
        };

        // ---- waterway ---------------------------------------------------------

        /// <summary>"River labels" for all classes from the schema's zooms; the class only orders the placement.</summary>
        public static List<OmtLabelRule> Waterway() => new List<OmtLabelRule>
        {
            R("river", "", 12, "hsl(210, 40%, 50%)", 3, 0).WithFont(OmtFontStyle.Italic).WithWrap(5), // schema: z3
            R("canal", "", 12, "hsl(210, 40%, 50%)", 12, 1).WithFont(OmtFontStyle.Italic).WithWrap(5), // schema: z12
            R("", "", 12, "hsl(210, 40%, 50%)", 13, 2).WithFont(OmtFontStyle.Italic).WithWrap(5), // schema: z13
        };

        // ---- park -------------------------------------------------------------

        /// <summary>"National park labels": parks of rank 1–2, zooms 7–12.</summary>
        public static List<OmtLabelRule> Park() => new List<OmtLabelRule>
        {
            R("", "", 12, "hsl(120, 100%, 25%)", 7, 0).WithFont(OmtFontStyle.Italic)
                .WithHalo(C("hsl(110, 83%, 88%)"), 0.3f).WithWrap(10).WithMaxZoom(12).WithMaxRank(2),
        };

        // ---- aeroway ----------------------------------------------------------

        /// <remarks>
        /// The style's "Airport labels" use the name, which the aeroway layer does not have; runways and taxiways are
        /// labeled with their <c>ref</c> instead.
        /// </remarks>
        public static List<OmtLabelRule> Aeroway() => new List<OmtLabelRule>
        {
            R("gate", "", 9, "hsl(0, 0%, 53%)", 16.5f, 0).WithHalo(Color.white, 1f).WithFont(OmtFontStyle.Bold), // schema: z14
            R("runway", "", 9, "hsl(0, 0%, 20%)", 14, 1).WithFont(OmtFontStyle.Italic),
            R("taxiway", "", 9, "hsl(0, 0%, 20%)", 14, 1).WithFont(OmtFontStyle.Italic),
        };

        // ---- transportation_name ----------------------------------------------

        /// <summary>Street names along the lines ("Road labels" from z11 and "Ferry labels"), by class.</summary>
        /// <remarks>
        /// Schema: motorway from z6, trunk from z8, other roads from z12, paths from z13. Priorities order the classes
        /// by importance (the style has one road label layer).
        /// </remarks>
        public static List<OmtLabelRule> TransportationNames()
        {
            Color halo = new Color(1, 1, 1, 0.97f);
            OmtLabelRule N(string cls, float minZoom, int priority) =>
                new OmtLabelRule(cls, "", 9, Color.black, minZoom, priority).WithHalo(halo, 1f);
            return new List<OmtLabelRule>
            {
                new OmtLabelRule("ferry", "", 10, C("hsl(240, 100%, 70%)"), 14, 13).WithHalo(C("hsl(195, 45%, 77%)"), 2f),
                N("motorway", 11, 5),
                N("trunk", 11, 6),
                N("primary", 12, 7),
                N("secondary", 12, 8),
                N("tertiary", 12, 9),
                N("minor", 12, 10),
                N("service", 12, 11),
                N("track", 12, 12),
                N("rail", 12, 12),
                N("transit", 12, 12),
                N("path", 13, 12),
                N("", 12, 12),
            };
        }

        /// <summary>Road shields (ref on the class's shield icon).</summary>
        /// <remarks>Schema: motorway refs from z6, trunk from z8, others from z12; the style shows shields from z9.</remarks>
        public static List<OmtLabelRule> TransportationShields()
        {
            OmtLabelRule S(string cls, string color, float minZoom, int priority) =>
                new OmtLabelRule(cls, "", 11, C(color), minZoom, priority) { HaloWidth = 0f, MaxWidth = 0f }
                    .WithIcon("road_" + cls, OmtTextAnchor.Center);
            return new List<OmtLabelRule>
            {
                S("motorway", "hsl(338, 87%, 21%)", 9, 0),
                S("trunk", "hsl(12, 77.6%, 21%)", 9, 1),
                S("primary", "hsl(36, 100%, 15%)", 12, 2),
                S("secondary", "hsl(69, 100%, 12%)", 12, 3),
                S("tertiary", "hsl(0, 0%, 23%)", 12, 4),
            };
        }

        /// <summary>Motorway junction (exit) labels at junction points (not styled; schema: z10).</summary>
        public static List<OmtLabelRule> TransportationJunctions() => new List<OmtLabelRule>
        {
            R("", "junction", 9, "hsl(338, 60%, 30%)", 14, 14),
        };

        // ---- transportation oneway arrows -------------------------------------

        /// <summary>Oneway arrows ("Oneway", "Oneway path") from z15.</summary>
        public static List<OmtLabelRule> OnewayArrows()
        {
            OmtLabelRule A(string cls, string icon, float scale) =>
                new OmtLabelRule(cls, "", 1, Color.white, 15, 20) { HaloWidth = 0f }.WithIcon(icon, OmtTextAnchor.Center, scale);
            return new List<OmtLabelRule>
            {
                A("path", "oneway-path", 0.7f),
                A("motorway", "oneway", 0.8f),
                A("trunk", "oneway", 0.8f),
                A("primary", "oneway", 0.8f),
                A("secondary", "oneway", 0.8f),
                A("tertiary", "oneway", 0.8f),
                A("minor", "oneway", 0.8f),
                A("service", "oneway", 0.8f),
            };
        }
    }
}
