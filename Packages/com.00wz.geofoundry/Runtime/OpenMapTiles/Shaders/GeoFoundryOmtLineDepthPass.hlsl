// GeoFoundry — DepthOnly and DepthNormals passes of GeoFoundry/OMT Line Lit
// ============================================================================
// Replace URP's DepthOnlyPass.hlsl / LitDepthNormalsPass.hlsl so that the depth and normals textures (SSAO, depth
// priming) see the ribbons at the same width and with the same dashes as the color passes (GeoFoundryOmtLineWidth.hlsl).
// Line materials use no alpha test, normal, parallax or detail maps, so those paths of the originals are omitted.
// ============================================================================
#ifndef GEOFOUNDRY_OMT_LINE_DEPTH_PASS_INCLUDED
#define GEOFOUNDRY_OMT_LINE_DEPTH_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif
#include "Packages/com.00wz.geofoundry/Runtime/OpenMapTiles/Shaders/GeoFoundryOmtLineWidth.hlsl"

struct OmtLineDepthAttributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float2 uv0          : TEXCOORD0;
    float4 uv3          : TEXCOORD3;
    float4 width        : TEXCOORD4;
    float2 uv5          : TEXCOORD5;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct OmtLineDepthVaryings
{
    float4 positionCS   : SV_POSITION;
    float3 dash         : TEXCOORD0;
    half3 normalWS      : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

OmtLineDepthVaryings OmtLineDepthVertex(OmtLineDepthAttributes input)
{
    OmtLineDepthVaryings output = (OmtLineDepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 positionOS = input.positionOS.xyz;
    float2 dash = OmtWidenLine(positionOS, input.normalOS, input.uv3, input.width, input.uv5);
    output.positionCS = TransformObjectToHClip(positionOS);
    output.dash = float3(input.uv0.y, dash);
    output.normalWS = half3(TransformObjectToWorldNormal(input.normalOS));
    return output;
}

half OmtLineDepthOnlyFragment(OmtLineDepthVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    OmtClipDash(input.dash);

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    return input.positionCS.z;
}

void OmtLineDepthNormalsFragment(
    OmtLineDepthVaryings input
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    OmtClipDash(input.dash);

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    #if defined(_GBUFFER_NORMALS_OCT)
        float3 normalWS = normalize(input.normalWS);
        float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
        float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
        half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);
        outNormalWS = half4(packedNormalWS, 0.0);
    #else
        outNormalWS = half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
    #endif

    #ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
    #endif
}

#endif
