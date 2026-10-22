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
// Occlusion. Both modes use the anchor pulled toward the camera by _DepthPull, so labels of POIs inside buildings
// stay visible:
//   Depth Texture (as MapLibre's calculate_visibility on terrain): the vertex shader compares the pulled anchor with
//     the camera depth texture at a few points around it and fades the whole label by the visible fraction; the quad
//     is then drawn at the near plane, so the ground and buildings never cut it. Needs the Depth Texture of the URP
//     asset; MapViewShaderGlobals publishes per camera whether it is available (_GeoFoundryDepthOcclusion).
//   Depth Buffer (also the fallback without the depth texture): the whole quad gets the depth of the pulled anchor
//     and is depth-tested per pixel, so labels behind hills and buildings are hidden, but parts of a label can be cut
//     where the ground or a building in front comes closer than the pulled anchor.
// ============================================================================
Shader "GeoFoundry/Label"
{
    Properties
    {
        [MainTexture] _MainTex("Atlas", 2D) = "white" {}
        _LabelScale("Size Multiplier", Range(0.25, 4)) = 1
        [Enum(Depth Buffer, 0, Depth Texture, 1)] _Occlusion("Occlusion", Float) = 1
        _DepthPull("Depth Pull (m)", Float) = 60
        _FadeRange("Zoom Fade Range", Range(0.05, 1)) = 0.25
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
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _LabelScale;
                float _Occlusion;
                float _DepthPull;
                float _FadeRange;
            CBUFFER_END

            // Set per camera by MapViewShaderGlobals: 1 if the camera depth texture is available.
            float _GeoFoundryDepthOcclusion;

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

            // Eye depth of the scene at a screen UV (sampled in the vertex shader, hence the explicit LOD; the UV is
            // mapped like SampleSceneDepth in DeclareDepthTexture.hlsl, which also handles render scaling).
            float SceneEyeDepth(float2 uv)
            {
                uv = ClampAndScaleUVForBilinear(UnityStereoTransformScreenSpaceTex(saturate(uv)),
                    _CameraDepthTexture_TexelSize.xy);
                float raw = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, sampler_PointClamp, uv, 0).r;
                if (unity_OrthoParams.w > 0.5)
                {
                #if UNITY_REVERSED_Z
                    raw = 1.0 - raw;
                #endif
                    return lerp(_ProjectionParams.y, _ProjectionParams.z, raw);
                }
                return LinearEyeDepth(raw, _ZBufferParams);
            }

            // Visible fraction of the anchor: the pulled anchor against the scene depth at the anchor and four points
            // 2 px around it, so a label on the edge of a building fades instead of popping.
            float AnchorVisibility(float4 anchorCS, float3 pulledWS)
            {
                float2 ndc = anchorCS.xy / anchorCS.w;
                float2 uv = ndc * 0.5 + 0.5;
                if (_ProjectionParams.x < 0.0) uv.y = 1.0 - uv.y; // flipped projection: back to texture space
                if (any(uv < 0.0) || any(uv > 1.0)) return 1.0;  // anchor off screen: nothing to test against

                float eye = -TransformWorldToView(pulledWS).z;
                float2 px = 2.0 / _ScreenParams.xy;
                float visible = 0.0;
                visible += step(eye, SceneEyeDepth(uv));
                visible += step(eye, SceneEyeDepth(uv + float2(px.x, 0)));
                visible += step(eye, SceneEyeDepth(uv - float2(px.x, 0)));
                visible += step(eye, SceneEyeDepth(uv + float2(0, px.y)));
                visible += step(eye, SceneEyeDepth(uv - float2(0, px.y)));
                return visible / 5.0;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 anchorWS = TransformObjectToWorld(input.positionOS);
                float4 clip = TransformWorldToHClip(anchorWS);

                // The anchor pulled toward the camera, for the occlusion test.
                float3 toCamera = GetCameraPositionWS() - anchorWS;
                float camDist = length(toCamera);
                float3 pulledWS = anchorWS + toCamera / max(camDist, 1e-3) * min(_DepthPull, camDist * 0.9);

                // Zoom fade.
                float zoom = DisplayZoom(anchorWS);
                float fade = saturate((zoom - input.uv1.z) / _FadeRange) * saturate((input.uv1.w - zoom) / _FadeRange);
                bool depthTexture = _Occlusion > 0.5 && _GeoFoundryDepthOcclusion > 0.5;
                if (depthTexture && fade > 0.0 && clip.w > 0.0) fade *= AnchorVisibility(clip, pulledWS);
                float4 pulledCS = TransformWorldToHClip(pulledWS);

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

                // Behind the camera, faded out or occluded: collapse the vertex outside the clip volume. Otherwise
                // the whole quad gets one depth: the near plane (occlusion already tested, never cut) or the pulled
                // anchor (tested per pixel against the depth buffer).
                if (clip.w <= 0.0 || fade <= 0.0)
                    clip = float4(0, 0, -2, 1);
                else if (depthTexture)
                    clip.z = UNITY_NEAR_CLIP_VALUE * 0.9999 * clip.w;
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
