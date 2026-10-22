using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>transportation</c> layer: roads, paths, railways, ferries, aerialways and piers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lines are styled by <c>class</c> and <c>subclass</c> (paths with <c>bicycle=designated</c> or
    /// <c>horse=designated</c> are drawn as cycleways or bridleways). All lines are one mesh drawn in groups: tunnels
    /// (<c>brunnel=tunnel</c>, faded with a dashed casing), ground-level roads, then bridges (<c>brunnel=bridge</c>,
    /// forced casing), each group ordered by <c>layer</c>; inside a group all casings are drawn first, then the fills
    /// by class importance, then overlays (rail hatching, construction dashes). <c>ramp=1</c> links and minor
    /// <c>service</c> roads (parking aisles, driveways) are narrower; <c>surface=unpaved</c>, <c>access=no</c> and
    /// <c>toll=1</c> can be tinted; <c>indoor</c> ways and ways on non-zero <c>level</c>s can be hidden. Oneway arrows
    /// (<c>oneway</c> = 1/-1) are optional icons along the lines.
    /// </para>
    /// <para>
    /// Polygons (pedestrian areas, bridge decks, platforms, piers) are a separate fill mesh. Attributes without a
    /// visual mapping in the style (<c>network</c>, <c>expressway</c>, <c>mtb_scale</c>, <c>foot</c>) are not used for
    /// rendering.
    /// </para>
    /// </remarks>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Transportation", fileName = "OmtTransportation")]
    public sealed class OmtTransportationHandler : OmtLayerHandler
    {
        [Tooltip("Line material (GeoFoundry/Line Lit). Recommended Sorting Priority: -30.")]
        [SerializeField] private Material _lineMaterial;

        [Tooltip("Area material (GeoFoundry/Surface Lit); leave empty to skip areas. Recommended Sorting Priority: -32.")]
        [SerializeField] private Material _areaMaterial;

        [Tooltip("Line styles by class/subclass.")]
        [SerializeField] private List<OmtLineRule> _lines = OmtStyleDefaults.TransportationLines();

        [Tooltip("Area styles by class/subclass.")]
        [SerializeField] private List<OmtFillRule> _areas = OmtStyleDefaults.TransportationAreas();

        [Header("Attributes")]
        [SerializeField] private OmtTunnelMode _tunnels = OmtTunnelMode.Dim;

        [Tooltip("Casing color of bridges whose rule has no casing.")]
        [SerializeField] private Color _bridgeCasing = OmtStyleDefaults.BridgeCasing;

        [Tooltip("Width multiplier of links (ramp=1).")]
        [SerializeField, Range(0.2f, 1f)] private float _rampWidth = 0.6f;

        [Tooltip("Width multiplier of minor service roads (parking_aisle, driveway, drive-through).")]
        [SerializeField, Range(0.2f, 1f)] private float _minorServiceWidth = 0.6f;

        [Tooltip("Tint of unpaved roads (surface=unpaved) toward a dirt color; 0 disables.")]
        [SerializeField, Range(0, 1)] private float _unpavedTint = 0.25f;

        [Tooltip("Fade of roads with restricted access (access=no) toward the background; 0 disables.")]
        [SerializeField, Range(0, 1)] private float _restrictedFade = 0.3f;

        [Tooltip("Tint of toll roads (toll=1) toward violet; 0 disables.")]
        [SerializeField, Range(0, 1)] private float _tollTint;

        [Tooltip("Hides indoor ways (indoor=1).")]
        [SerializeField] private bool _hideIndoor = true;

        [Tooltip("Hides ways on a level other than 0 (level attribute of steps and footways).")]
        [SerializeField] private bool _hideOtherLevels;

        [Header("Oneway arrows")]
        [Tooltip("Label settings providing the icon atlas; leave empty to skip oneway arrows.")]
        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _onewayArrows = OmtLabelDefaults.OnewayArrows();

        public override string LayerName => "transportation";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_areaMaterial != null)
            {
                var areaMods = new List<OmtModifier>();
                // Areas inside tunnels are not drawn, like in the style.
                areaMods.Add(OmtModifier.Create("brunnel", "tunnel", OmtModifierAction.Skip));
                output.Add(await OmtBuild.FillsAsync(ctx, _areas, "class", "subclass", areaMods, "TransportationAreas"), _areaMaterial);
            }

            if (_lineMaterial != null && !ctx.Ct.IsCancellationRequested)
                output.Add(await BuildLinesAsync(ctx), _lineMaterial);

            if (_text != null && _onewayArrows != null && _onewayArrows.Count > 0)
            {
                await OmtLabels.BuildAsync(ctx, _text, new[]
                {
                    new OmtLabelPass(_onewayArrows, job =>
                    {
                        job.Points = false;
                        job.Lines = true;
                        job.TextKind = OmtLabelText.None;
                        job.IconMode = OmtIconMode.Marker;
                        job.RequireKey = OmtRules.Fixed("oneway");
                        job.ReverseKey = OmtRules.Fixed("oneway");
                        job.Flags = OmtLabelFlags.AllowOverlap | OmtLabelFlags.KeepDirection;
                        job.Spacing = 95f;
                        return job;
                    }),
                }, output, "OnewayArrows");
            }
        }

        private async Awaitable<Mesh> BuildLinesAsync(LayerLoadContext ctx)
        {
            var rules = OmtRules.ToNative(_lines, Allocator.Persistent);
            var items = new NativeList<LineDrawItem>(256, Allocator.Persistent);
            try
            {
                var job = new OmtTransportationItemsJob
                {
                    GeomTypes = ctx.Layer.FeatureGeomTypesSlice,
                    Props = OmtProps.From(ctx.Layer),
                    Rules = rules,
                    Zoom = ctx.TileCoord.Z,
                    MercatorScale = OmtZoom.MercatorScale(ctx.TileCoord),
                    Tunnels = _tunnels,
                    BridgeCasing = OmtColor.ToVertex(_bridgeCasing),
                    White = OmtColor.ToVertex(Color.white),
                    Black = OmtColor.ToVertex(Color.black),
                    Dirt = OmtColor.ToVertex(OmtColor.Parse("hsl(35, 45%, 60%)")),
                    Background = OmtColor.ToVertex(OmtStyleDefaults.Background),
                    Violet = OmtColor.ToVertex(OmtColor.Parse("hsl(280, 50%, 60%)")),
                    RampWidth = _rampWidth,
                    MinorServiceWidth = _minorServiceWidth,
                    UnpavedTint = _unpavedTint,
                    RestrictedFade = _restrictedFade,
                    TollTint = _tollTint,
                    HideIndoor = _hideIndoor,
                    HideOtherLevels = _hideOtherLevels,
                    Output = items,
                };
                if (!await job.Schedule().WaitNonBlockingAsync(ctx.Ct) || items.Length == 0) return null;

                var mesh = await AsyncGeometryWorker.BuildLineMeshAsync(
                    ctx.Layer, items.AsArray(), ctx.TileCoord, ctx.Grids.Center, ctx.LocalFrame,
                    OmtZoom.MetersPerPixel(ctx.TileCoord.Z), ctx.Ct);
                if (mesh != null) mesh.name = $"Transportation_{ctx.TileCoord}";
                return mesh;
            }
            finally
            {
                rules.Dispose();
                items.Dispose();
            }
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials()
        {
            CheckPriority(_lineMaterial, OmtDrawOrder.Transportation, "Line Material");
            CheckPriority(_areaMaterial, OmtDrawOrder.TransportationAreas, "Area Material");
        }
#endif
    }

    /// <summary>
    /// Builds the ordered draw list of the transportation lines; see <see cref="OmtTransportationHandler"/> for the
    /// ordering and attribute rules.
    /// </summary>
    [BurstCompile]
    internal struct OmtTransportationItemsJob : IJob
    {
        [ReadOnly] public NativeArray<MvtGeomType> GeomTypes;
        public OmtProps Props;
        [ReadOnly] public NativeArray<OmtLineRuleData> Rules;
        public int Zoom;
        public float MercatorScale;
        public OmtTunnelMode Tunnels;
        public Color32 BridgeCasing, White, Black, Dirt, Background, Violet;
        public float RampWidth, MinorServiceWidth, UnpavedTint, RestrictedFade, TollTint;
        public bool HideIndoor, HideOtherLevels;

        public NativeList<LineDrawItem> Output;

        private struct Entry
        {
            public int Group;   // 0 tunnel, 1 ground, 2 bridge
            public int Layer;
            public int Order;
            public int Fi;
            public int Rule;
            public LineWidth Width;
            public Color32 Color;
            public bool Casing;
            public Color32 CasingColor;
            public float2 CasingDash;
        }

        private struct EntryComparer : System.Collections.Generic.IComparer<Entry>
        {
            public int Compare(Entry a, Entry b)
            {
                int c = a.Group.CompareTo(b.Group);
                if (c != 0) return c;
                c = a.Layer.CompareTo(b.Layer);
                if (c != 0) return c;
                c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : a.Fi.CompareTo(b.Fi);
            }
        }

        public void Execute()
        {
            FixedString64Bytes kClass = "class", kSub = "subclass", kBrunnel = "brunnel", kLayer = "layer";
            FixedString64Bytes kRamp = "ramp", kService = "service", kSurface = "surface", kAccess = "access";
            FixedString64Bytes kToll = "toll", kIndoor = "indoor", kLevel = "level", kBicycle = "bicycle", kHorse = "horse";
            FixedString64Bytes vTunnel = "tunnel", vBridge = "bridge", vPath = "path", vDesignated = "designated";
            FixedString64Bytes vService = "service", vUnpaved = "unpaved", vNo = "no";
            FixedString128Bytes sCycleway = "cycleway", sBridleway = "bridleway";
            FixedString64Bytes vAisle = "parking_aisle", vDriveway = "driveway", vDriveThrough = "drive-through";

            var entries = new NativeList<Entry>(GeomTypes.Length, Allocator.Temp);

            for (int fi = 0; fi < GeomTypes.Length; fi++)
            {
                if (GeomTypes[fi] != MvtGeomType.LineString) continue;
                if (HideIndoor && Props.IsTrue(fi, kIndoor)) continue;
                if (HideOtherLevels && Props.GetNumber(fi, kLevel, 0) != 0) continue;

                bool hasClass = Props.TryGetText(fi, kClass, out var cls);
                bool hasSub = Props.TryGetText(fi, kSub, out var sub);
                if (hasClass && cls.Equals(vPath))
                {
                    if (Props.Is(fi, kBicycle, vDesignated)) { sub = sCycleway; hasSub = true; }
                    else if (Props.Is(fi, kHorse, vDesignated)) { sub = sBridleway; hasSub = true; }
                }
                int ri = OmtRules.Match(Rules, cls, hasClass, sub, hasSub);
                if (ri < 0) continue;
                var rule = Rules[ri];
                if (!rule.Visible || Zoom < rule.MinZoom) continue;

                var e = new Entry
                {
                    Group = 1,
                    Layer = (int)Props.GetLong(fi, kLayer, 0),
                    Order = rule.Order,
                    Fi = fi,
                    Rule = ri,
                    Width = OmtZoom.LineWidth(rule.WidthMeters, rule.MinWidthPixels, MercatorScale),
                    Color = rule.Color,
                    Casing = rule.HasCasing,
                    CasingColor = rule.CasingColor,
                };

                if (Props.Is(fi, kBrunnel, vTunnel))
                {
                    if (Tunnels == OmtTunnelMode.Hide) continue;
                    e.Group = 0;
                    e.Layer = math.min(e.Layer, -1);
                    if (Tunnels == OmtTunnelMode.Dim)
                    {
                        e.Color = OmtModifiers.Mix(e.Color, White, 0.45f);
                        e.CasingDash = new float2(1f, 0.5f);
                    }
                }
                else if (Props.Is(fi, kBrunnel, vBridge))
                {
                    e.Group = 2;
                    e.Layer = math.max(e.Layer, 1);
                    e.CasingColor = rule.HasCasing ? OmtModifiers.Mix(rule.CasingColor, Black, 0.4f) : BridgeCasing;
                    e.Casing = true;
                }

                if (Props.GetNumber(fi, kRamp, 0) == 1) e.Width = e.Width.ScaleCore(RampWidth);
                if (hasClass && cls.Equals(vService) &&
                    (Props.Is(fi, kService, vAisle) || Props.Is(fi, kService, vDriveway) || Props.Is(fi, kService, vDriveThrough)))
                    e.Width = e.Width.ScaleCore(MinorServiceWidth);
                if (UnpavedTint > 0f && Props.Is(fi, kSurface, vUnpaved)) e.Color = OmtModifiers.Mix(e.Color, Dirt, UnpavedTint);
                if (TollTint > 0f && Props.IsTrue(fi, kToll)) e.Color = OmtModifiers.Mix(e.Color, Violet, TollTint);
                if (RestrictedFade > 0f && Props.Is(fi, kAccess, vNo)) e.Color = OmtModifiers.Mix(e.Color, Background, RestrictedFade);

                entries.Add(e);
            }

            entries.Sort(new EntryComparer());

            int start = 0;
            while (start < entries.Length)
            {
                int end = start + 1;
                while (end < entries.Length && entries[end].Group == entries[start].Group && entries[end].Layer == entries[start].Layer)
                    end++;
                EmitGroup(entries, start, end);
                start = end;
            }

            entries.Dispose();
        }

        private void EmitGroup(NativeList<Entry> entries, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                var e = entries[i];
                if (!e.Casing) continue;
                var rule = Rules[e.Rule];
                float bridge = e.Group == 2 ? 1f : 0f;
                var w = e.Width.WithBorder(math.max(rule.CasingMeters, bridge) * MercatorScale,
                    math.max(rule.CasingPixels, bridge));
                Output.Add(new LineDrawItem(e.Fi, w, e.CasingColor, e.CasingDash, LineDrawFlags.None, rule.MinZoom));
            }
            for (int i = start; i < end; i++)
            {
                var e = entries[i];
                var rule = Rules[e.Rule];
                Output.Add(new LineDrawItem(e.Fi, e.Width, e.Color, rule.Dash, LineDrawFlags.None, rule.MinZoom));
            }
            for (int i = start; i < end; i++)
            {
                var e = entries[i];
                var rule = Rules[e.Rule];
                if (!rule.HasOverlay || rule.OverlayWidth <= 0f) continue;
                Output.Add(new LineDrawItem(e.Fi, e.Width.ScaleCore(rule.OverlayWidth), rule.OverlayColor,
                    rule.OverlayDash / rule.OverlayWidth, LineDrawFlags.None, rule.MinZoom));
            }
        }
    }
}
