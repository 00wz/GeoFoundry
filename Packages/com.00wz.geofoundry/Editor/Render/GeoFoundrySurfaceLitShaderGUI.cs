using System;
using UnityEditor;
using UnityEngine;

namespace GeoFoundry.Editor
{
    /// <summary>
    /// Inspector of <c>GeoFoundry/Surface Lit</c>: URP's Lit inspector plus the "Deferred Blend" toggle, which also sets
    /// the GBuffer pass's blend factors (<c>_GBufferSrcBlend</c>, <c>_GBufferDstBlend</c> for color,
    /// <c>_GBufferSrcBlendAlpha</c>, <c>_GBufferDstBlendAlpha</c> for the alpha channels, which must keep the value
    /// below) and the <c>_GEOFOUNDRY_GBUFFER_BLEND</c> keyword.
    /// </summary>
    /// <remarks>
    /// URP's Lit inspector (<c>UnityEditor.Rendering.Universal.ShaderGUI.LitShader</c>) is internal, so it is created
    /// by reflection and every call is forwarded to it; without it the default property list is drawn.
    /// </remarks>
    public sealed class GeoFoundrySurfaceLitShaderGUI : ShaderGUI
    {
        private const string LitShaderGuiType =
            "UnityEditor.Rendering.Universal.ShaderGUI.LitShader, Unity.RenderPipelines.Universal.Editor";

        private const string DeferredBlend = "_DeferredBlend";
        private const string Keyword = "_GEOFOUNDRY_GBUFFER_BLEND";

        private static readonly GUIContent DeferredBlendText = EditorGUIUtility.TrTextContent("Deferred Blend",
            "Blends semi-transparent vertex colors into the GBuffer in the deferred path, like URP's GBuffer decals " +
            "(smoothness, occlusion and material flags stay those of the surface below). Enable it for layers drawn " +
            "over a base; leave it off for the base layer itself (the terrain). The forward path always blends.");

        private readonly ShaderGUI _lit = CreateLitGui();

        private static ShaderGUI CreateLitGui()
        {
            var type = Type.GetType(LitShaderGuiType);
            return type != null ? Activator.CreateInstance(type, true) as ShaderGUI : null;
        }

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            if (_lit == null)
            {
                base.OnGUI(materialEditor, properties);
                return;
            }

            _lit.OnGUI(materialEditor, properties);

            var blend = FindProperty(DeferredBlend, properties, false);
            if (blend == null) return;
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            materialEditor.ShaderProperty(blend, DeferredBlendText);
            if (EditorGUI.EndChangeCheck())
                foreach (var target in materialEditor.targets)
                    if (target is Material material) Apply(material);
        }

        public override void ValidateMaterial(Material material)
        {
            _lit?.ValidateMaterial(material);
            Apply(material);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            if (_lit != null) _lit.AssignNewShaderToMaterial(material, oldShader, newShader);
            else base.AssignNewShaderToMaterial(material, oldShader, newShader);
            Apply(material);
        }

        public override void OnClosed(Material material) => _lit?.OnClosed(material);

        /// <summary>Sets the GBuffer blend state of <paramref name="material"/> from its "Deferred Blend" toggle.</summary>
        public static void Apply(Material material)
        {
            if (material == null || !material.HasProperty(DeferredBlend)) return;
            bool on = material.GetFloat(DeferredBlend) > 0.5f;
            material.SetFloat("_GBufferSrcBlend", (float)(on ? UnityEngine.Rendering.BlendMode.SrcAlpha : UnityEngine.Rendering.BlendMode.One));
            material.SetFloat("_GBufferDstBlend", (float)(on ? UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : UnityEngine.Rendering.BlendMode.Zero));
            material.SetFloat("_GBufferSrcBlendAlpha", (float)(on ? UnityEngine.Rendering.BlendMode.Zero : UnityEngine.Rendering.BlendMode.One));
            material.SetFloat("_GBufferDstBlendAlpha", (float)(on ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.Zero));
            if (on) material.EnableKeyword(Keyword);
            else material.DisableKeyword(Keyword);
        }
    }
}
