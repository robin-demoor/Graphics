#ifndef UNITY_BSDF_INCLUDED
#define UNITY_BSDF_INCLUDED

#include "Common.hlsl"

//-----------------------------------------------------------------------------
// Fresnel term
//-----------------------------------------------------------------------------

float F_Schlick(float f0, float f90, float u)
{
    float x		= 1.0 - u;
    float x5	= x * x;
    x5			= x5 * x5 * x;
    return (f90 - f0) * x5 + f0; // sub mul mul mul sub mad
}

float F_Schlick(float f0, float u)
{
    return F_Schlick(f0, 1.0, u);
}

float3 F_Schlick(float3 f0, float f90, float u)
{
    float x		= 1.0 - u;
    float x5	= x * x;
    x5			= x5 * x5 * x;
    return (float3(f90, f90, f90) - f0) * x5 + f0; // sub mul mul mul sub mad
}

float3 F_Schlick(float3 f0, float u)
{
    return F_Schlick(f0, 1.0, u);
}

//-----------------------------------------------------------------------------
// Specular BRDF
//-----------------------------------------------------------------------------

// With analytical light (not image based light) we clamp the minimun roughness in the NDF to avoid numerical instability.
#define UNITY_MIN_ROUGHNESS 0.002

float D_GGX(float NdotH, float roughness)
{
    roughness = max(roughness, UNITY_MIN_ROUGHNESS);
    float a2 = roughness * roughness;
    float f = (NdotH * a2 - NdotH) * NdotH + 1.0;
    return INV_PI * a2 / (f * f);
}

// Ref: http://jcgt.org/published/0003/02/03/paper.pdf
float V_SmithJointGGX(float NdotL, float NdotV, float roughness)
{
#if 1
    // Original formulation:
    //	lambda_v	= (-1 + sqrt(a2 * (1 - NdotL2) / NdotL2 + 1)) * 0.5f;
    //	lambda_l	= (-1 + sqrt(a2 * (1 - NdotV2) / NdotV2 + 1)) * 0.5f;
    //	G			= 1 / (1 + lambda_v + lambda_l);

    // Reorder code to be more optimal
    float a = roughness;
    float a2 = a * a;

    float lambdaV = NdotL * sqrt((-NdotV * a2 + NdotV) * NdotV + a2);
    float lambdaL = NdotV * sqrt((-NdotL * a2 + NdotL) * NdotL + a2);

    // Simplify visibility term: (2.0f * NdotL * NdotV) /  ((4.0f * NdotL * NdotV) * (lambda_v + lambda_l));
    return 0.5f / (lambdaV + lambdaL);
#else
    // Approximation of the above formulation (simplify the sqrt, not mathematically correct but close enough)
    float a = roughness;
    float lambdaV = NdotL * (NdotV * (1 - a) + a);
    float lambdaL = NdotV * (NdotL * (1 - a) + a);

    return 0.5 / (lambdaV + lambdaL);
#endif
}

// roughnessT -> roughness in tangent direction
// roughnessB -> roughness in bitangent direction
float D_GGXAniso(float TdotH, float BdotH, float NdotH, float roughnessT, float roughnessB)
{
    roughnessT = max(roughnessT, UNITY_MIN_ROUGHNESS);
    roughnessB = max(roughnessB, UNITY_MIN_ROUGHNESS);

    float f = TdotH * TdotH / (roughnessT * roughnessT) + BdotH * BdotH / (roughnessB * roughnessB) + NdotH * NdotH;
    return INV_PI / (roughnessT * roughnessB * f * f);
}

// Ref: https://cedec.cesa.or.jp/2015/session/ENG/14698.html The Rendering Materials of Far Cry 4
float V_SmithJointGGXAniso(float TdotV, float BdotV, float NdotV, float TdotL, float BdotL, float NdotL, float roughnessT, float roughnessB)
{
    float aT = roughnessT;
    float aT2 = aT * aT;
    float aB = roughnessB;
    float aB2 = aB * aB;

    float lambdaV = NdotL * sqrt(aT2 * TdotV * TdotV + aB2 * BdotV * BdotV + NdotV * NdotV);
    float lambdaL = NdotV * sqrt(aT2 * TdotL * TdotL + aB2 * BdotL * BdotL + NdotL * NdotL);

    return 0.5 / (lambdaV + lambdaL);
}

// TODO: Optimize, lambdaV could be precomputed at the beginning of the loop and reuse for all lights.

//-----------------------------------------------------------------------------
// Diffuse BRDF - diffuseColor is expected to be multiply by the caller
//-----------------------------------------------------------------------------

float Lambert()
{
    return INV_PI;
}

float DisneyDiffuse(float NdotV, float NdotL, float LdotH, float perceptualRoughness)
{
    float fd90 = 0.5 + 2 * LdotH * LdotH * perceptualRoughness;
    // Two schlick fresnel term
    float lightScatter = F_Schlick(1.0, fd90, NdotL);
    float viewScatter = F_Schlick(1.0, fd90, NdotV);

    return INV_PI * lightScatter * viewScatter;
}

#endif // UNITY_BSDF_INCLUDED
