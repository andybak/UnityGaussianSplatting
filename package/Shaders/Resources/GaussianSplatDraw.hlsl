// SPDX-License-Identifier: MIT
#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"
uint _SplatViewStride, _SplatSamples;

StructuredBuffer<uint> _OrderBuffer;
float2 _SplatScreenSize;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    nointerpolation uint idx : TEXCOORD1;
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

// Separate constant buffer avoids Unity's DX12 stale-uniform bug (IN-99220).
cbuffer SplatGlobalUniforms // match struct SplatGlobalUniforms in C#
{
    uint sgu_transparencyMode;
    uint sgu_frameOffset;
}

StructuredBuffer<SplatViewData> _SplatViewData;

struct SplatVertex { uint vertex : SV_VertexID; uint instanceID : SV_InstanceID; };
v2f vert (SplatVertex input)
{
    v2f o = (v2f)0;
    uint instID = input.instanceID;
    uint vtxID = input.vertex;
    uint viewOffset = 0;
#if defined(SPLAT_DIRECT) && (defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED))
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
#if defined(UNITY_STEREO_INSTANCING_ENABLED)
    instID = unity_InstanceID;
#endif
    viewOffset = unity_StereoEyeIndex * _SplatViewStride;
#endif
#ifndef SPLAT_DIRECT
    if (sgu_transparencyMode == 0)
        instID = _OrderBuffer[instID];
#endif
    o.idx = instID + sgu_frameOffset;
	SplatViewData view = _SplatViewData[viewOffset + instID];
	// test "is this +inf" sentinel bits exactly
	bool culled = asuint(view.pos.x) == 0x7f800000u;
	if (culled)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;

		float2 axis1 = float2(f16tof32(view.axis1), f16tof32(view.axis1 >> 16));
		float2 axis2 = float2(f16tof32(view.axis2), f16tof32(view.axis2 >> 16));
		float2 deltaScreenPos = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _SplatScreenSize;
		o.vertex = float4(view.pos, 1);
		o.vertex.xy += deltaScreenPos;
	}
	#ifndef SPLAT_DIRECT
    FlipProjectionIfBackbuffer(o.vertex);
#endif
    return o;
}

// Hash Functions for GPU Rendering
// https://jcgt.org/published/0009/03/02/
uint3 pcg3d16(uint3 v)
{
    v = v * 12829u + 47989u;

    v.x += v.y*v.z;
    v.y += v.z*v.x;
    v.z += v.x*v.y;

    v.x += v.y*v.z;
    v.y += v.z*v.x;
    v.z += v.x*v.y;

	v >>= 16u;
    return v;
}

struct FragOutput
{
    half4 color : SV_Target0;
#ifdef SPLAT_MSAA
    uint coverage : SV_Coverage;
#endif
#if SPLAT_REPROJECTED
    float depth : SV_Target1;
#endif
};

FragOutput frag (v2f i)
{
	uint coverage = 0;
    float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	alpha = saturate(alpha * i.col.a);

    if (alpha < 1.0/255.0)
        discard;

#ifndef SPLAT_DIRECT
    if (sgu_transparencyMode == 0)
    {
        i.col.rgb *= alpha;
    }
    else
#endif
    {
        // "Hashed Alpha Testing", Wyman, McGuire 2017
        // https://casual-effects.com/research/Wyman2017Hashed/index.html
        uint3 hash = pcg3d16(uint3(uint2(i.vertex.xy), i.idx));
        float cutoff = hash.x * (1.0 / 65536.0);
#ifdef SPLAT_MSAA
        // Randomly round the sample count, then rotate the mask independently per splat.
        uint count = (uint)(alpha * _SplatSamples + cutoff);
        uint shift = hash.y & (_SplatSamples - 1);
        uint mask = ((1u << count) - 1u) << shift;
        coverage = (mask | (mask >> _SplatSamples)) & ((1u << _SplatSamples) - 1u);
        if (coverage == 0)
#else
        if (alpha <= cutoff)
#endif
            discard;
        alpha = 1;
    }
    FragOutput res;
#ifdef SPLAT_MSAA
    res.coverage = coverage;
#endif
    res.color = half4(i.col.rgb, alpha);
#if SPLAT_REPROJECTED
    res.depth = i.vertex.z;
#endif
    return res;
}
