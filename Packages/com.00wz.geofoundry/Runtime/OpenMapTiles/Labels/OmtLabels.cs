using System;
using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Handlers;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Native inputs of a label pass, created on the main thread before the request job is scheduled: rules, name
    /// keys, icon ids and the request list. Disposed after the label mesh is built.
    /// </summary>
    internal sealed class OmtLabelInputs : IDisposable
    {
        public NativeArray<OmtLabelRuleData> Rules;
        public NativeArray<FixedString64Bytes> NameKeys;
        public NativeHashMap<FixedString64Bytes, int> IconIds;
        public NativeList<OmtLabelRequest> Requests;
        private readonly bool _ownsIcons;

        public OmtLabelInputs(OmtTextSettings settings, IReadOnlyList<OmtLabelRule> rules)
        {
            Rules = OmtLabelRules.ToNative(rules, settings.Scale, Allocator.Persistent);
            NameKeys = OmtRules.ToNative(settings.NameKeys, Allocator.Persistent);
            if (settings.CanRenderIcons)
            {
                IconIds = settings.Icons.Ids;
            }
            else
            {
                IconIds = new NativeHashMap<FixedString64Bytes, int>(1, Allocator.Persistent);
                _ownsIcons = true;
            }
            Requests = new NativeList<OmtLabelRequest>(64, Allocator.Persistent);
        }

        /// <summary>Creates a general request job over the layer, prefilled with these inputs.</summary>
        public OmtLabelRequestJob CreateRequestJob(LayerLoadContext ctx, NativeArray<OmtModifier> modifiers) =>
            new OmtLabelRequestJob
            {
                GeomTypes = ctx.Layer.FeatureGeomTypesSlice,
                Props = OmtProps.From(ctx.Layer),
                Rules = Rules,
                NameKeys = NameKeys,
                IconIds = IconIds,
                Modifiers = modifiers,
                ClassKey = OmtRules.Fixed("class"),
                Points = true,
                Spacing = 250f,
                Output = Requests,
            };

        public void Dispose()
        {
            if (Rules.IsCreated) Rules.Dispose();
            if (NameKeys.IsCreated) NameKeys.Dispose();
            if (_ownsIcons && IconIds.IsCreated) IconIds.Dispose();
            if (Requests.IsCreated) Requests.Dispose();
        }
    }

    /// <summary>
    /// Label pipeline shared by the label-producing handlers: after the handler's request job, collects the glyphs,
    /// fills the atlases (main thread), runs <see cref="OmtLabelLayoutJob"/> and assembles one mesh with a submesh
    /// per texture (icons, glyph atlases) rendered by the <c>GeoFoundry/Label</c> shader.
    /// </summary>
    /// <summary>One request pass of a label build: its rules and the configuration of the general request job.</summary>
    internal sealed class OmtLabelPass
    {
        public IReadOnlyList<OmtLabelRule> Rules;
        public Func<OmtLabelRequestJob, OmtLabelRequestJob> Configure;
        public List<OmtModifier> Modifiers;

        public OmtLabelPass(IReadOnlyList<OmtLabelRule> rules, Func<OmtLabelRequestJob, OmtLabelRequestJob> configure,
            List<OmtModifier> modifiers = null)
        {
            Rules = rules;
            Configure = configure;
            Modifiers = modifiers;
        }
    }

    internal static class OmtLabels
    {
        /// <summary>
        /// Runs one or more request passes of <see cref="OmtLabelRequestJob"/> (chained, sharing one rule array and
        /// one request list) and builds the label mesh into <paramref name="output"/>. Does nothing without
        /// <paramref name="settings"/> or rules.
        /// </summary>
        public static async Awaitable BuildAsync(LayerLoadContext ctx, OmtTextSettings settings,
            IReadOnlyList<OmtLabelPass> passes, OmtTileOutput output, string name, bool occupy = true)
        {
            if (settings == null || passes == null || ctx.Ct.IsCancellationRequested) return;

            var allRules = new List<OmtLabelRule>();
            foreach (var pass in passes)
                if (pass?.Rules != null) allRules.AddRange(pass.Rules);
            if (allRules.Count == 0) return;

            var disposables = new List<IDisposable>();
            using var inputs = new OmtLabelInputs(settings, allRules);
            try
            {
                JobHandle handle = default;
                int offset = 0;
                foreach (var pass in passes)
                {
                    if (pass?.Rules == null || pass.Rules.Count == 0) continue;
                    var mods = OmtModifiers.ToNative(pass.Modifiers ?? new List<OmtModifier>(), Allocator.Persistent);
                    disposables.Add(mods);
                    var job = pass.Configure != null
                        ? pass.Configure(inputs.CreateRequestJob(ctx, mods))
                        : inputs.CreateRequestJob(ctx, mods);
                    job.Rules = inputs.Rules.GetSubArray(offset, pass.Rules.Count);
                    job.RuleOffset = offset;
                    handle = job.Schedule(handle);
                    offset += pass.Rules.Count;
                }
                await BuildAsync(ctx, settings, inputs, handle, output, name, occupy);
            }
            finally
            {
                foreach (var d in disposables) d.Dispose();
            }
        }

        /// <summary>
        /// Awaits <paramref name="requestJob"/> (writing <see cref="OmtLabelInputs.Requests"/>), places the labels
        /// and adds the label mesh to <paramref name="output"/>. Disposes nothing: the caller owns
        /// <paramref name="inputs"/>.
        /// </summary>
        public static async Awaitable BuildAsync(
            LayerLoadContext ctx, OmtTextSettings settings, OmtLabelInputs inputs, JobHandle requestJob,
            OmtTileOutput output, string name, bool occupy = true)
        {
            if (!await requestJob.WaitNonBlockingAsync(ctx.Ct) || inputs.Requests.Length == 0) return;

            var keys = new NativeHashSet<int>(256, Allocator.Persistent);
            NativeHashMap<int, OmtGlyphInfo> glyphs = default;
            NativeArray<OmtFontMetrics> metrics = default;
            NativeArray<OmtIconInfo> emptyIcons = default;
            var positions = new NativeList<float3>(256, Allocator.Persistent);
            var vertices = new NativeList<OmtLabelVertex>(256, Allocator.Persistent);
            var colors = new NativeList<OmtLabelColors>(256, Allocator.Persistent);
            var indices = new NativeList<int>(384, Allocator.Persistent);
            var subMeshes = new NativeList<int3>(4, Allocator.Persistent);
            var bounds = new NativeArray<float3>(2, Allocator.Persistent);
            try
            {
                var requests = inputs.Requests.AsArray();
                if (settings.CanRenderText)
                {
                    var collect = new OmtCollectGlyphsJob { Requests = requests, Rules = inputs.Rules, Keys = keys };
                    if (!await collect.Schedule().WaitNonBlockingAsync(ctx.Ct)) return;
                }

                var cache = settings.CanRenderText ? settings.Glyphs : null;
                glyphs = cache != null ? cache.Prepare(keys, Allocator.Persistent)
                    : new NativeHashMap<int, OmtGlyphInfo>(1, Allocator.Persistent);
                metrics = cache != null ? cache.CreateMetrics(Allocator.Persistent)
                    : new NativeArray<OmtFontMetrics>(3, Allocator.Persistent);
                NativeArray<OmtIconInfo> icons;
                if (settings.CanRenderIcons) icons = settings.Icons.Infos;
                else icons = emptyIcons = new NativeArray<OmtIconInfo>(0, Allocator.Persistent);

                int z = ctx.TileCoord.Z;
                var layout = new OmtLabelLayoutJob
                {
                    Requests = requests,
                    Rules = inputs.Rules,
                    Glyphs = glyphs,
                    Metrics = metrics,
                    Icons = icons,
                    GeomTypes = ctx.Layer.FeatureGeomTypesSlice,
                    FeatureRingRange = ctx.Layer.FeatureRingRangeSlice,
                    Rings = ctx.Layer.Rings,
                    RingPoints = ctx.Layer.RingPoints,
                    Coord = ctx.TileCoord,
                    Extent = ctx.Layer.Extent,
                    Grid = ctx.Grids.Center,
                    LocalFrame = ctx.LocalFrame,
                    Lift = settings.AnchorLift,
                    TileZoom = z,
                    Tiers = z >= settings.SourceMaxZoom ? settings.OverzoomLevels : 1,
                    Padding = settings.CollisionPadding,
                    Occupancy = settings.GetOccupancy(ctx.TileRoot),
                    Occupy = occupy,
                    Positions = positions,
                    Vertices = vertices,
                    Colors = colors,
                    Indices = indices,
                    SubMeshes = subMeshes,
                    Bounds = bounds,
                };
                if (!await layout.Schedule().WaitNonBlockingAsync(ctx.Ct) || positions.Length == 0) return;

                var materials = new List<Material>(subMeshes.Length);
                var kept = new List<int3>(subMeshes.Length);
                foreach (var sm in subMeshes)
                {
                    var material = settings.GetSlotMaterial(sm.x);
                    if (material == null) continue;
                    materials.Add(material);
                    kept.Add(sm);
                }
                if (kept.Count == 0) return;

                var mesh = BuildMesh(positions, vertices, colors, indices, kept, bounds, ctx.TileCoord.SizeMeters());
                mesh.name = $"{name}_{ctx.TileCoord}";
                output.Add(mesh, materials.ToArray());
            }
            finally
            {
                keys.Dispose();
                if (glyphs.IsCreated) glyphs.Dispose();
                if (metrics.IsCreated) metrics.Dispose();
                if (emptyIcons.IsCreated) emptyIcons.Dispose();
                positions.Dispose();
                vertices.Dispose();
                colors.Dispose();
                indices.Dispose();
                subMeshes.Dispose();
                bounds.Dispose();
            }
        }

        /// <summary>
        /// Assembles the label mesh in three vertex streams (within the 4-stream limit of WebGL): stream 0 Position =
        /// anchor; stream 1 TexCoord0 = (uv, mode, halo px), TexCoord1 = (offset px, min zoom, max zoom), TexCoord2 =
        /// (tangent, rotation mode); stream 2 Color = text color, TexCoord3 = halo color. 32-bit indices, one submesh
        /// per kept texture slot. Bounds are padded, since the quads expand in screen space.
        /// </summary>
        private static Mesh BuildMesh(
            NativeList<float3> positions, NativeList<OmtLabelVertex> vertices, NativeList<OmtLabelColors> colors,
            NativeList<int> indices, List<int3> subMeshes, NativeArray<float3> bounds, double tileSize)
        {
            int vertexCount = positions.Length;
            int indexCount = 0;
            foreach (var sm in subMeshes) indexCount += sm.z;

            var dataArray = Mesh.AllocateWritableMeshData(1);
            var data = dataArray[0];
            data.SetVertexBufferParams(vertexCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.UNorm8, 4, 2));
            data.SetIndexBufferParams(indexCount, IndexFormat.UInt32);

            data.GetVertexData<float3>(0).CopyFrom(positions.AsArray());
            data.GetVertexData<OmtLabelVertex>(1).CopyFrom(vertices.AsArray());
            data.GetVertexData<OmtLabelColors>(2).CopyFrom(colors.AsArray());

            var dst = data.GetIndexData<int>();
            var src = indices.AsArray();
            int at = 0;
            data.subMeshCount = subMeshes.Count;
            for (int i = 0; i < subMeshes.Count; i++)
            {
                var sm = subMeshes[i];
                NativeArray<int>.Copy(src, sm.y, dst, at, sm.z);
                data.SetSubMesh(i, new SubMeshDescriptor(at, sm.z), MeshUpdateFlags.DontRecalculateBounds);
                at += sm.z;
            }

            var mesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(dataArray, mesh, MeshUpdateFlags.DontRecalculateBounds);
            float3 min = bounds[0], max = bounds[1];
            float margin = (float)(tileSize * 0.25) + 100f;
            var b = new Bounds((Vector3)((min + max) * 0.5f), (Vector3)(max - min));
            b.Expand(margin * 2f);
            mesh.bounds = b;
            return mesh;
        }
    }
}
