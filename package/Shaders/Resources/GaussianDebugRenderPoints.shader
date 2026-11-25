// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Debug/Render Points"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite On
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "GaussianSplatting.hlsl"

struct v2f
{
    half3 color : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

float _SplatSize;
float4x4 _SplatMatrixVP;
float2 _SplatScreenSize;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    uint splatIndex = instID;

    SplatData splat = LoadSplatData(splatIndex);

    float3 centerWorldPos = splat.pos;
    centerWorldPos = mul(unity_ObjectToWorld, float4(centerWorldPos,1)).xyz;

    float4 centerClipPos = mul(_SplatMatrixVP, float4(centerWorldPos, 1));

    o.vertex = centerClipPos;
	uint idx = vtxID;
    float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
    o.vertex.xy += (quadPos * _SplatSize / _SplatScreenSize) * o.vertex.w;

    o.color.rgb = saturate(splat.sh.col);
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    return half4(i.color.rgb, 1);
}
ENDCG
        }
        Pass
        {
            ZWrite On
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#pragma multi_compile_instancing
#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

struct v2f
{
    half3 color : TEXCOORD0;
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

float _SplatSize;
float4x4 _SplatLocalToWorld;
float2 _SplatScreenSize;

struct Vertex { uint vertex : SV_VertexID; uint instanceID : SV_InstanceID; };
v2f vert (Vertex input)
{
    v2f o = (v2f)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    uint splatIndex = input.instanceID;
#if defined(UNITY_STEREO_INSTANCING_ENABLED)
    splatIndex = unity_InstanceID;
#endif

    SplatData splat = LoadSplatData(splatIndex);

    float3 centerWorldPos = splat.pos;
    centerWorldPos = mul(_SplatLocalToWorld, float4(centerWorldPos,1)).xyz;

    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));

    o.vertex = centerClipPos;
	uint idx = input.vertex;
    float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
    o.vertex.xy += (quadPos * _SplatSize / _SplatScreenSize) * o.vertex.w;

    o.color.rgb = saturate(splat.sh.col);
    o.color.rgb = GammaToLinearSpace(o.color.rgb);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    return half4(i.color.rgb, 1);
}
ENDCG
        }
    }
}
