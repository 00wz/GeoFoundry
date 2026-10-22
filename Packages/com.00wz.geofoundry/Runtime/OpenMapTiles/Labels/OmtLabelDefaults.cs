using System.Collections.Generic;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Default label styles of the OpenMapTiles handlers, transcribed from the symbol layers of the "OSM
    /// OpenMapTiles" style (CC-BY 4.0): text colors, halos, sizes (the style's size around zoom 14), font faces,
    /// icons and minimum zooms. Classes the style does not label get neutral defaults.
    /// </summary>
    /// <remarks>
    /// A minimum zoom is never below the tile zoom from which the OpenMapTiles schema (3.x, the reference SQL of
    /// <c>layers/*/*.sql</c>) puts the class into the tiles (marked "schema: zN" below). A label promised earlier than
    /// its data could not fade in with the zoom: it would pop up at full opacity when the tile that carries it loads.
    /// </remarks>
    public static class OmtLabelDefaults
    {
        private static Color C(string css) => OmtColor.Parse(css);

        private static readonly Color Halo = new Color(1f, 1f, 1f, 0.8f);

        private static OmtLabelRule R(string cls, string sub, float size, string color, float minZoom, int priority = 0) =>
            new OmtLabelRule(cls, sub, size, C(color), minZoom, priority).WithHalo(Halo, 1f);

        // ---- place ------------------------------------------------------------

        /// <summary>Place rules; matched by class and capital (<c>capital</c> is the subclass key of the handler).</summary>
        public static List<OmtLabelRule> Place() => new List<OmtLabelRule>
        {
            R("continent", "", 14, "hsl(0, 0%, 20%)", 0, 0).WithFont(OmtFontStyle.Bold, OmtTextTransform.Uppercase).WithMaxZoom(4),
            R("country", "", 15, "hsl(302, 16%, 36%)", 2, 1).WithFont(OmtFontStyle.Bold).WithWrap(6.25f).WithMaxZoom(12),
            R("state", "", 12, "hsl(302, 18%, 42%)", 4, 2).WithHalo(new Color(1, 1, 1, 0.7f), 0.8f).WithMaxZoom(12),
            R("province", "", 12, "hsl(302, 18%, 42%)", 4, 2).WithHalo(new Color(1, 1, 1, 0.7f), 0.8f).WithMaxZoom(12),
            R("city", "2", 16, "hsl(0, 0%, 13%)", 3, 3).WithHalo(Halo, 1.2f).WithIcon("place-capital-8", OmtTextAnchor.Bottom),
            R("city", "1", 16, "hsl(0, 0%, 13%)", 3, 3).WithHalo(Halo, 1.2f).WithIcon("place-capital-8", OmtTextAnchor.Bottom),
            R("city", "", 14, "hsl(0, 0%, 20%)", 4, 4).WithIcon("place-6", OmtTextAnchor.Bottom, 0.8f),
            R("town", "", 13, "hsl(0, 0%, 20%)", 7, 5).WithHalo(Halo, 1.2f), // schema: z7
            R("village", "", 12, "hsl(0, 0%, 20%)", 9, 6).WithHalo(Halo, 1.2f),
            R("suburb", "", 12, "hsl(0, 0%, 40%)", 11, 7).WithHalo(Color.white, 1.5f),
            R("borough", "", 12, "hsl(0, 0%, 40%)", 11, 7).WithHalo(Color.white, 1.5f),
            R("quarter", "", 12, "hsl(0, 0%, 47%)", 14, 8).WithHalo(Color.white, 1.5f), // schema: z14
            R("neighbourhood", "", 11, "hsl(0, 0%, 47%)", 14, 8).WithHalo(Color.white, 1.5f), // schema: z14
            R("hamlet", "", 11, "hsl(0, 0%, 27%)", 12, 8).WithHalo(Color.white, 1.5f),
            R("isolated_dwelling", "", 10, "hsl(0, 0%, 33%)", 14, 9).WithHalo(Color.white, 1.5f),
            R("island", "", 12, "hsl(0, 0%, 27%)", 8, 7).WithFont(OmtFontStyle.Italic).WithHalo(Color.white, 1.5f),
            R("islet", "", 11, "hsl(0, 0%, 27%)", 12, 8).WithFont(OmtFontStyle.Italic).WithHalo(Color.white, 1.5f),
            R("aboriginal_lands", "", 11, "hsl(30, 40%, 38%)", 8, 7).WithFont(OmtFontStyle.Italic),
            R("", "", 11, "hsl(0, 0%, 30%)", 12, 9),
        };

        // ---- poi --------------------------------------------------------------

        /// <summary>POI rules; icons are named after the subclass, falling back to the class.</summary>
        public static List<OmtLabelRule> Poi()
        {
            var rules = new List<OmtLabelRule>();

            void Group(string color, float minZoom, int priority, string icon, params string[] classes)
            {
                foreach (var cls in classes)
                    rules.Add(R(cls, "", 11, color, minZoom, priority).WithIcon(icon).WithWrap(8));
            }

            // Specific subclasses first.
            rules.Add(R("railway", "", 11, "hsl(232, 41%, 48%)", 12, 0).WithIcon("square_train").WithWrap(8));
            // schema: z12–13 carry only railway stations, halts, ferry terminals and large universities; the rest z14.
            rules.Add(R("zoo", "zoo", 12, "hsl(330, 100%, 20%)", 14, 1).WithIcon("{subclass}").WithWrap(8));
            rules.Add(R("shop", "mall", 12, "hsl(7, 100%, 41%)", 15, 1).WithWrap(8));
            rules.Add(R("park", "park", 11, "hsl(125, 83%, 28%)", 15, 2).WithWrap(8));
            rules.Add(R("bus", "bus_stop", 11, "hsl(216, 100%, 50%)", 17, 3).WithIcon("bus_stop.12").WithWrap(8));
            rules.Add(R("ferry_terminal", "", 11, "hsl(261, 46%, 43%)", 15, 1).WithIcon("ferry").WithWrap(8));
            rules.Add(R("", "books", 11, "hsl(300, 50%, 40%)", 15, 2).WithIcon("{subclass}").WithWrap(8));
            rules.Add(R("", "car_parts", 11, "hsl(300, 50%, 40%)", 15, 2).WithIcon("{subclass}").WithWrap(8));
            rules.Add(R("", "supermarket", 11, "hsl(300, 50%, 40%)", 15, 2).WithIcon("{subclass}").WithWrap(8));

            Group("hsl(300, 50%, 40%)", 15, 2, "{subclass}",
                "alcohol_shop", "bakery", "bicycle", "butcher", "clothing_store", "grocery", "hairdresser", "music",
                "seafood", "shop");
            Group("hsl(37, 87%, 24%)", 18, 4, "{subclass}", "recycling", "waste_basket", "drinking_water", "toilets");
            Group("hsl(129, 24%, 23%)", 14, 2, "", "cemetery");
            Group("hsl(60, 100%, 15%)", 16, 2, "", "college", "kindergarten", "school", "university");
            Group("hsl(126, 100%, 27%)", 16, 3, "{subclass}", "pitch", "playground", "stile", "garden", "gate");
            Group("hsl(126, 100%, 27%)", 16, 2, "{subclass}", "golf", "stadium", "swimming_pool", "sports_centre", "water_park");
            Group("hsl(35, 100%, 39%)", 16, 2, "{subclass}", "ice_cream", "cafe", "beer", "bar", "fast_food", "restaurant");
            Group("hsl(37, 87%, 24%)", 16, 2, "{subclass}",
                "atm", "town_hall", "post", "library", "police", "information", "cinema", "theatre", "fire_station");
            Group("hsl(37, 87%, 24%)", 15, 1, "{subclass}", "museum", "castle", "monument", "art_gallery", "arts_centre", "gallery");
            Group("hsl(330, 100%, 20%)", 15, 1, "", "attraction");
            Group("hsl(216, 100%, 50%)", 17, 3, "{subclass}", "bicycle_parking", "car", "fuel", "parking");
            Group("hsl(0, 100%, 37%)", 16, 2, "{class}", "hospital", "pharmacy", "dentist", "veterinary");
            Group("hsl(216, 100%, 50%)", 16, 2, "camping", "campsite");
            Group("hsl(216, 100%, 50%)", 17, 3, "{subclass}", "lodging", "bus");
            Group("hsl(240, 12%, 25%)", 16, 2, "{subclass}", "place_of_worship");
            rules.Add(R("harbor", "", 13, "hsl(230, 68%, 61%)", 16, 2).WithWrap(8));
            rules.Add(R("", "", 11, "hsl(0, 0%, 35%)", 16, 4).WithIcon("{subclass}").WithWrap(8));
            return rules;
        }

        // ---- housenumber ------------------------------------------------------

        public static List<OmtLabelRule> Housenumber() => new List<OmtLabelRule>
        {
            R("", "", 10, "hsl(0, 0%, 40%)", 17, 10).WithWrap(0),
        };

        // ---- mountain_peak ----------------------------------------------------

        public static List<OmtLabelRule> MountainPeak() => new List<OmtLabelRule>
        {
            R("volcano", "", 10, "hsl(0, 100%, 42%)", 11, 0).WithIcon("volcano").WithWrap(6),
            R("saddle", "", 10, "hsl(29, 57%, 27%)", 12, 1).WithIcon("saddle").WithWrap(6),
            R("peak", "", 10, "hsl(29, 57%, 27%)", 11, 0).WithIcon("peak").WithWrap(6),
            R("ridge", "", 10, "hsl(29, 40%, 35%)", 13, 2).WithFont(OmtFontStyle.Italic),
            R("arete", "", 10, "hsl(29, 40%, 35%)", 13, 2).WithFont(OmtFontStyle.Italic),
            R("cliff", "", 10, "hsl(0, 0%, 30%)", 14, 3).WithFont(OmtFontStyle.Italic),
            R("", "", 10, "hsl(29, 57%, 27%)", 12, 1).WithIcon("peak").WithWrap(6),
        };

        // ---- aerodrome_label --------------------------------------------------

        public static List<OmtLabelRule> AerodromeLabel()
        {
            var rules = new List<OmtLabelRule>();
            string[] classes = { "international", "public", "regional", "military", "private", "other" };
            for (int i = 0; i < classes.Length; i++)
            {
                // schema: z8–9 carry only international airports with an IATA code, the rest from z10.
                float minZoom = i == 0 ? 8f : Mathf.Max(10f, 8f + i * 0.75f);
                rules.Add(R(classes[i], "", i == 0 ? 12 : 11, "hsl(261, 46%, 43%)", minZoom, i)
                    .WithFont(OmtFontStyle.Italic).WithIcon("aerodrome.12").WithWrap(9));
            }
            rules.Add(R("", "", 11, "hsl(261, 46%, 43%)", 12, 6).WithFont(OmtFontStyle.Italic).WithIcon("aerodrome.12").WithWrap(9));
            return rules;
        }

        // ---- water_name -------------------------------------------------------

        public static List<OmtLabelRule> WaterName() => new List<OmtLabelRule>
        {
            R("ocean", "", 16, "hsl(210, 39%, 45%)", 0, 0).WithFont(OmtFontStyle.Italic).WithHalo(new Color(1, 1, 1, 0.6f), 1f).WithWrap(6),
            R("sea", "", 13, "hsl(210, 39%, 45%)", 3, 1).WithFont(OmtFontStyle.Italic).WithHalo(new Color(1, 1, 1, 0.6f), 1f).WithWrap(6),
            R("bay", "", 11, "hsl(210, 39%, 49%)", 13, 2).WithFont(OmtFontStyle.Italic).WithWrap(5), // schema: z13
            R("strait", "", 11, "hsl(210, 39%, 49%)", 8, 2).WithFont(OmtFontStyle.Italic).WithWrap(5),
            R("lake", "", 11, "hsl(210, 39%, 49%)", 9, 3).WithWrap(5),
            R("", "", 11, "hsl(210, 39%, 49%)", 12, 4).WithWrap(5),
        };

        /// <summary>Labels along lake center lines.</summary>
        public static List<OmtLabelRule> WaterNameLines() => new List<OmtLabelRule>
        {
            R("", "", 12, "hsl(238, 43%, 55%)", 3, 3).WithHalo(new Color(1, 1, 1, 0.7f), 1f), // schema: z3
        };

        // ---- waterway ---------------------------------------------------------

        public static List<OmtLabelRule> Waterway() => new List<OmtLabelRule>
        {
            R("river", "", 11, "hsl(210, 40%, 50%)", 9, 0).WithFont(OmtFontStyle.Italic),
            R("canal", "", 11, "hsl(210, 40%, 50%)", 12, 1).WithFont(OmtFontStyle.Italic),
            R("stream", "", 10, "hsl(210, 40%, 50%)", 14, 2).WithFont(OmtFontStyle.Italic),
            R("", "", 10, "hsl(210, 40%, 50%)", 15, 3).WithFont(OmtFontStyle.Italic),
        };

        // ---- park -------------------------------------------------------------

        public static List<OmtLabelRule> Park() => new List<OmtLabelRule>
        {
            R("", "", 12, "hsl(120, 100%, 25%)", 7, 0).WithFont(OmtFontStyle.Italic)
                .WithHalo(C("hsl(110, 83%, 88%)"), 1f).WithWrap(8),
        };

        // ---- aeroway ----------------------------------------------------------

        public static List<OmtLabelRule> Aeroway() => new List<OmtLabelRule>
        {
            R("gate", "", 10, "hsl(0, 0%, 53%)", 16.5f, 1).WithHalo(Color.white, 1f),
            R("runway", "", 11, "hsl(0, 0%, 20%)", 14, 0).WithFont(OmtFontStyle.Italic),
            R("taxiway", "", 10, "hsl(0, 0%, 20%)", 15, 1).WithFont(OmtFontStyle.Italic),
        };

        // ---- transportation_name ----------------------------------------------

        /// <summary>Street names along the lines, by class.</summary>
        public static List<OmtLabelRule> TransportationNames()
        {
            Color halo = new Color(1, 1, 1, 0.97f);
            OmtLabelRule N(string cls, string sub, float size, float minZoom, int priority) =>
                new OmtLabelRule(cls, sub, size, Color.black, minZoom, priority).WithHalo(halo, 1f);
            return new List<OmtLabelRule>
            {
                new OmtLabelRule("ferry", "", 11, C("hsl(240, 100%, 70%)"), 14, 5).WithHalo(C("hsl(195, 45%, 77%)"), 2f),
                N("motorway", "", 12, 11, 0),
                N("trunk", "", 12, 11, 1),
                N("primary", "", 12, 12, 2),
                N("secondary", "", 11, 12.5f, 3),
                N("tertiary", "", 11, 13, 4),
                N("minor", "", 11, 14, 5),
                N("service", "", 10, 15.5f, 6),
                N("path", "", 10, 15.5f, 7),
                N("track", "", 10, 15.5f, 7),
                N("rail", "", 10, 15, 6),
                N("transit", "", 10, 15, 6),
                N("", "", 10, 15, 7),
            };
        }

        /// <summary>Road shields (ref on the class's shield icon).</summary>
        public static List<OmtLabelRule> TransportationShields()
        {
            OmtLabelRule S(string cls, string color, float minZoom, int priority) =>
                new OmtLabelRule(cls, "", 10, C(color), minZoom, priority) { HaloWidth = 0f, MaxWidth = 0f }
                    .WithIcon("road_" + cls, OmtTextAnchor.Center);
            return new List<OmtLabelRule>
            {
                S("motorway", "hsl(338, 87%, 21%)", 9, 0),
                S("trunk", "hsl(12, 77.6%, 21%)", 9, 1),
                S("primary", "hsl(36, 100%, 15%)", 10, 2),
                S("secondary", "hsl(69, 100%, 12%)", 11, 3),
                S("tertiary", "hsl(0, 0%, 23%)", 12, 4),
            };
        }

        /// <summary>Motorway junction (exit) labels at junction points.</summary>
        public static List<OmtLabelRule> TransportationJunctions() => new List<OmtLabelRule>
        {
            R("", "junction", 9, "hsl(338, 60%, 30%)", 14, 3),
        };

        // ---- transportation oneway arrows -------------------------------------

        public static List<OmtLabelRule> OnewayArrows() => new List<OmtLabelRule>
        {
            new OmtLabelRule("path", "", 1, Color.white, 16, 20) { HaloWidth = 0f }.WithIcon("oneway-path", OmtTextAnchor.Center, 0.7f),
            new OmtLabelRule("motorway", "", 1, Color.white, 15, 20) { HaloWidth = 0f }.WithIcon("oneway", OmtTextAnchor.Center, 0.8f),
            new OmtLabelRule("trunk", "", 1, Color.white, 15, 20) { HaloWidth = 0f }.WithIcon("oneway", OmtTextAnchor.Center, 0.8f),
            new OmtLabelRule("primary", "", 1, Color.white, 15, 20) { HaloWidth = 0f }.WithIcon("oneway", OmtTextAnchor.Center, 0.8f),
            new OmtLabelRule("secondary", "", 1, Color.white, 15, 20) { HaloWidth = 0f }.WithIcon("oneway", OmtTextAnchor.Center, 0.8f),
            new OmtLabelRule("tertiary", "", 1, Color.white, 15, 20) { HaloWidth = 0f }.WithIcon("oneway", OmtTextAnchor.Center, 0.8f),
            new OmtLabelRule("minor", "", 1, Color.white, 15.5f, 20) { HaloWidth = 0f }.WithIcon("oneway", OmtTextAnchor.Center, 0.8f),
            new OmtLabelRule("service", "", 1, Color.white, 16, 20) { HaloWidth = 0f }.WithIcon("oneway", OmtTextAnchor.Center, 0.8f),
        };
    }
}
