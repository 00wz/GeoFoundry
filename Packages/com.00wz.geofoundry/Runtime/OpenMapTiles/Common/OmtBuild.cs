using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Geometry;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// One pass of line item generation: a rule table matched by <see cref="ClassKey"/>/<see cref="SubclassKey"/> over
    /// LineString features and/or Polygon outlines, with attribute modifiers. Several passes of one handler write
    /// into the same draw lists in pass order.
    /// </summary>
    internal sealed class OmtLinePass
    {
        public IReadOnlyList<OmtLineRule> Rules;
        public string ClassKey = "class";
        public string SubclassKey = "";
        public bool Lines = true;
        public bool Outlines;
        public List<OmtModifier> Modifiers = new List<OmtModifier>();
    }

    /// <summary>
    /// Shared build steps of the polygon and line handlers: run the item job over the borrowed layer, then the
    /// geometry builder, awaiting everything (including on cancellation) and releasing the temporary data.
    /// </summary>
    internal static class OmtBuild
    {
        /// <summary>
        /// Builds the fill mesh of a polygon layer from <paramref name="rules"/>. Returns <c>null</c> if nothing
        /// matched or the load was cancelled.
        /// </summary>
        public static async Awaitable<Mesh> FillsAsync(
            LayerLoadContext ctx, IReadOnlyList<OmtFillRule> rules, string classKey, string subclassKey,
            List<OmtModifier> modifiers, string name)
        {
            if (rules == null || rules.Count == 0) return null;

            var nativeRules = OmtRules.ToNative(rules, Allocator.Persistent);
            var nativeMods = OmtModifiers.ToNative(modifiers ?? new List<OmtModifier>(), Allocator.Persistent);
            var items = new NativeList<SurfaceDrawItem>(64, Allocator.Persistent);
            try
            {
                var job = new OmtFillItemsJob
                {
                    GeomTypes = ctx.Layer.FeatureGeomTypesSlice,
                    Props = OmtProps.From(ctx.Layer),
                    Rules = nativeRules,
                    Modifiers = nativeMods,
                    ClassKey = OmtRules.Fixed(classKey),
                    SubclassKey = OmtRules.Fixed(subclassKey),
                    Zoom = ctx.TileCoord.Z,
                    Output = items,
                };
                if (!await job.Schedule().WaitNonBlockingAsync(ctx.Ct) || items.Length == 0) return null;

                var mesh = await AsyncGeometryWorker.BuildSurfaceMeshAsync(
                    ctx.Layer, items.AsArray(), ctx.TileCoord, ctx.Grids.Center, ctx.LocalFrame, 1f, ctx.Ct);
                if (mesh != null) mesh.name = $"{name}_{ctx.TileCoord}";
                return mesh;
            }
            finally
            {
                nativeRules.Dispose();
                nativeMods.Dispose();
                items.Dispose();
            }
        }

        /// <summary>
        /// Builds the line meshes of a layer from one or more passes. The primary mesh holds all regular features;
        /// the secondary one those flagged with <see cref="OmtModifierAction.Secondary"/> (null if none).
        /// </summary>
        public static async Awaitable<(Mesh primary, Mesh secondary)> LinesAsync(
            LayerLoadContext ctx, IReadOnlyList<OmtLinePass> passes, string name)
        {
            var primary = new NativeList<LineDrawItem>(64, Allocator.Persistent);
            var secondary = new NativeList<LineDrawItem>(8, Allocator.Persistent);
            try
            {
                if (!await ScheduleLinePassesAsync(ctx, passes, primary, secondary)) return (null, null);

                Mesh a = null, b = null;
                if (primary.Length > 0)
                {
                    a = await AsyncGeometryWorker.BuildLineMeshAsync(
                        ctx.Layer, primary.AsArray(), ctx.TileCoord, ctx.Grids.Center, ctx.LocalFrame,
                        OmtZoom.MetersPerPixel(ctx.TileCoord.Z), ctx.Ct);
                    if (a != null) a.name = $"{name}_{ctx.TileCoord}";
                }
                if (secondary.Length > 0 && !ctx.Ct.IsCancellationRequested)
                {
                    b = await AsyncGeometryWorker.BuildLineMeshAsync(
                        ctx.Layer, secondary.AsArray(), ctx.TileCoord, ctx.Grids.Center, ctx.LocalFrame,
                        OmtZoom.MetersPerPixel(ctx.TileCoord.Z), ctx.Ct);
                    if (b != null) b.name = $"{name}Secondary_{ctx.TileCoord}";
                }
                return (a, b);
            }
            finally
            {
                primary.Dispose();
                secondary.Dispose();
            }
        }

        /// <summary>
        /// Runs the item jobs of all passes (chained, appending to the same lists) and awaits them. Returns
        /// <c>false</c> on cancellation.
        /// </summary>
        public static async Awaitable<bool> ScheduleLinePassesAsync(
            LayerLoadContext ctx, IReadOnlyList<OmtLinePass> passes,
            NativeList<LineDrawItem> primary, NativeList<LineDrawItem> secondary)
        {
            var disposables = new List<System.IDisposable>();
            try
            {
                float scale = OmtZoom.MercatorScale(ctx.TileCoord);
                Unity.Jobs.JobHandle handle = default;
                foreach (var pass in passes)
                {
                    if (pass?.Rules == null || pass.Rules.Count == 0) continue;
                    var rules = OmtRules.ToNative(pass.Rules, Allocator.Persistent);
                    var mods = OmtModifiers.ToNative(pass.Modifiers, Allocator.Persistent);
                    disposables.Add(rules);
                    disposables.Add(mods);
                    handle = new OmtLineItemsJob
                    {
                        GeomTypes = ctx.Layer.FeatureGeomTypesSlice,
                        Props = OmtProps.From(ctx.Layer),
                        Rules = rules,
                        Modifiers = mods,
                        ClassKey = OmtRules.Fixed(pass.ClassKey),
                        SubclassKey = OmtRules.Fixed(pass.SubclassKey),
                        Zoom = ctx.TileCoord.Z,
                        MercatorScale = scale,
                        IncludeLines = pass.Lines,
                        OutlinePolygons = pass.Outlines,
                        Primary = primary,
                        Secondary = secondary,
                    }.Schedule(handle);
                }
                return await handle.WaitNonBlockingAsync(ctx.Ct);
            }
            finally
            {
                foreach (var d in disposables) d.Dispose();
            }
        }
    }
}
