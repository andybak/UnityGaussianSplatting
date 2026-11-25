// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    Properties
    {
        _SrcBlend("Src Blend", Float) = 8 // OneMinusDstAlpha
        _DstBlend("Dst Blend", Float) = 1 // One
        _ZWrite("ZWrite", Float) = 0 // Off
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite [_ZWrite]
            Blend [_SrcBlend] [_DstBlend]
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#pragma multi_compile_local __ SPLAT_REPROJECTED

#include "GaussianSplatDraw.hlsl"
ENDCG
        }
        Pass
        {
            ZWrite [_ZWrite]
            Blend [_SrcBlend] [_DstBlend]
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#pragma multi_compile_instancing
#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
#define SPLAT_DIRECT 1

#include "GaussianSplatDraw.hlsl"
ENDCG
        }
        Pass
        {
            ZWrite [_ZWrite]
            Blend [_SrcBlend] [_DstBlend]
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#pragma multi_compile_instancing
#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
#define SPLAT_DIRECT 1
#define SPLAT_MSAA 1

#include "GaussianSplatDraw.hlsl"
ENDCG
        }
    }
}
