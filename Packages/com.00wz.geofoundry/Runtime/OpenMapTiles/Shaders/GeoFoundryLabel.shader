// GeoFoundry — Label
// ============================================================================
// Screen-space map labels and icons of constant pixel size (OmtLabelLayoutJob / OmtLabels). Every vertex carries
// the label anchor as its position plus:
//   TEXCOORD0 = (u, v, mode, halo width px)      mode 0: SDF glyph (alpha atlas), 1: RGBA icon
//   TEXCOORD1 = (offset x px, offset y px, min zoom, max zoom)
//   TEXCOORD2 = (tangent xyz, rotation)          rotation 0: screen-aligned, 1: along the tangent kept upright,
//                                                2: along the tangent without flipping (arrows)
//   COLOR     = text color (icons: white)
//   TEXCOORD3 = halo color
// The vertex shader projects the anchor, rotates the pixel offset to the tangent's screen direction if requested
// and adds it in clip space. The display zoom at the anchor (from the camera distance and field of view, in the
// MapLibre convention where the world is 512 * 2^zoom pixels wide and scene units are Web Mercator meters) fades
// the label in at its min zoom and out at its max zoom, which is how decluttering tiers take effect.
// Depth: the whole quad uses the depth of the anchor pulled toward the camera by _DepthPull, so labels inside
// buildings stay visible while labels behind hills are hidden; set _ZTest to Always to never hide labels.
// ============================================================================
Shader "GeoFoundry/Label"
{
    Properties
    {
        [MainTexture] _MainTex("Atlas", 2D) = "white" {}
        _LabelScale("Size Multiplier", Range(0.25, 4)) = 1
        _DepthPull("Depth Pull (m)", Float) = 60
        _FadeRange("Zoom Fade Range", Range(0.05, 1)) = 0.25
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("ZTest", Float) = 4
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+100"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "Label"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _LabelScale;
                float _DepthPull;
                float _FadeRange;
                float _ZTest;
            CBUFFER_END

            #define WORLD_SIZE_METERS 40075016.685578488
            #define TILE_PIXELS 512.0

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 uv0        : TEXCOORD0;
                float4 uv1        : TEXCOORD1;
                float4 uv2        : TEXCOORD2;
                half4  color      : COLOR;
                half4  halo       : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv         : TEXCOORD0; // uv, mode, halo px
                half4  color      : TEXCOORD1;
                half4  halo       : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float DisplayZoom(float3 positionWS)
            {
                float3 positionVS = TransformWorldToView(positionWS);
                float screenHeight = _ScreenParams.y;
                float mpp;
                if (unity_OrthoParams.w > 0.5)
                    mpp = 2.0 * unity_OrthoParams.y / screenHeight;
                else
                    // abs: the projection is flipped when rendering into a texture on some platforms.
                    mpp = 2.0 * max(-positionVS.z, 1e-3) / (abs(UNITY_MATRIX_P._m11) * screenHeight);
                return log2(WORLD_SIZE_METERS / (TILE_PIXELS * mpp));
            }

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 anchorWS = TransformObjectToWorld(input.positionOS);
                float4 clip = TransformWorldToHClip(anchorWS);

                // Depth of the anchor pulled toward the camera.
                float3 toCamera = GetCameraPositionWS() - anchorWS;
                float camDist = length(toCamera);
                float3 pulledWS = anchorWS + toCamera / max(camDist, 1e-3) * min(_DepthPull, camDist * 0.9);
                float4 pulledCS = TransformWorldToHClip(pulledWS);

                // Zoom fade.
                float zoom = DisplayZoom(anchorWS);
                float fade = saturate((zoom - input.uv1.z) / _FadeRange) * saturate((input.uv1.w - zoom) / _FadeRange);

                // Pixel offset, optionally rotated to the screen direction of the tangent.
                float2 offset = input.uv1.xy * _LabelScale;
                float rotation = input.uv2.w;
                if (rotation > 0.5)
                {
                    float3 tangentWS = TransformObjectToWorldDir(input.uv2.xyz, false);
                    float4 aheadCS = TransformWorldToHClip(anchorWS + tangentWS * max(camDist * 0.01, 0.1));
                    float2 dir = (aheadCS.xy / aheadCS.w - clip.xy / clip.w) * _ScreenParams.xy;
                    dir.y *= _ProjectionParams.x; // to screen space with y up, also when rendering flipped
                    float len = length(dir);
                    dir = len > 1e-5 ? dir / len : float2(1, 0);
                    if (rotation < 1.5 && dir.x < 0.0) dir = -dir; // keep upright
                    offset = offset.x * dir + offset.y * float2(-dir.y, dir.x);
                }

                // Pixels → clip space (NDC spans 2 units across the screen); undo the y flip of render textures.
                offset.y *= _ProjectionParams.x;
                clip.xy += offset * (2.0 / _ScreenParams.xy) * clip.w;

                // Behind the camera or faded out: collapse the vertex outside the clip volume.
                if (clip.w <= 0.0 || fade <= 0.0)
                    clip = float4(0, 0, -2, 1);
                else
                    clip.z = pulledCS.z / pulledCS.w * clip.w;

                output.positionCS = clip;
                output.uv = input.uv0;
                output.color = input.color;
                output.color.a *= fade;
                output.halo = input.halo;
                output.halo.a *= fade;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv.xy);

                if (input.uv.z > 0.5)
                {
                    // Icon: the atlas is sRGB-imported, so the sample is already linear.
                    half4 c = tex * input.color;
                    clip(c.a - 0.004);
                    return c;
                }

                // SDF glyph: the distance in screen pixels from the glyph edge, estimated from the derivatives.
                float d = tex.a;
                float w = max(fwidth(d), 1e-5);
                float sd = (d - 0.5) / w;
                float fillA = saturate(sd + 0.5);
                float haloA = saturate(sd + input.uv.w * _LabelScale + 0.5) * (input.uv.w > 0.0 ? 1.0 : 0.0);

                float a = fillA * input.color.a + haloA * input.halo.a * (1.0 - fillA * input.color.a);
                clip(a - 0.004);
                half3 rgb = (input.color.rgb * fillA * input.color.a +
                             input.halo.rgb * haloA * input.halo.a * (1.0 - fillA * input.color.a)) / max(a, 1e-4);
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
