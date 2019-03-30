#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/VolumeRendering.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/PbrSky/PbrSkyRenderer.cs.hlsl"

CBUFFER_START(UnityPbrSky)
    // All the entries use km and 1/km units.
    float  _PlanetaryRadius;
    float  _PlanetaryRadiusSquared;
    float  _AtmosphericDepth;
    float  _RcpAtmosphericDepth;

    float  _AtmosphericRadiusSquared;
    float  _GrazingAngleAtmosphereExitDistance;

    float  _AirDensityFalloff;
    float  _AirScaleHeight;
    float  _AerosolDensityFalloff;
    float  _AerosolScaleHeight;

    float3 _SunRadiance; // TODO: isn't that just a global multiplier?

    float3 _AirSeaLevelExtinction;
    float  _AerosolSeaLevelExtinction;
CBUFFER_END

TEXTURE2D(_OpticalDepthTexture);
TEXTURE2D(_GroundIrradianceTexture);
SAMPLER(s_linear_clamp_sampler);

float IntersectAtmosphereFromInside(float height, float cosChi)
{
    float R = _PlanetaryRadius;
    float h = height;
    float r = R + h;

    // r_o = float2(0, r)
    // r_d = float2(sinChi, cosChi)
    // p_s = r_o + t * r_d
    //
    // (R + H)^2 = dot(r_o + t * r_d, r_o + t * r_d)
    // (R + H)^2 = ((r_o + t * r_d).x)^2 + ((r_o + t * r_d).y)^2
    // (R + H)^2 = t^2 + 2 * dot(r_o, r_d) + dot(r_o, r_o)
    //
    // t^2 + 2 * dot(r_o, r_d) + dot(r_o, r_o) - (R + H)^2 = 0
    //
    // Solve: t^2 + (2 * b) * t + c = 0.
    //
    // We are only interested in the largest root (the other one is negative).
    // t = (-2 * b + sqrt((2 * b)^2 - 4 * c)) / 2
    // t = -b + sqrt(b^2 - c)
    // t = -b + sqrt(d)
    //
    float b = r * cosChi;
    float c = r * r - _AtmosphericRadiusSquared;
    float d = b * b - c;

    return -b + sqrt(d);
}

float MapQuadraticHeight(float height)
{
    // TODO: we should adjust sub-texel coordinates
    // to account for the non-linear height distribution.
    return sqrt(height * _RcpAtmosphericDepth);
}

// Returns the height.
float UnmapQuadraticHeight(float v)
{
    return (v * v) * _AtmosphericDepth;
}

// See Figure 3 in Bruneton's paper.
float2 MapAerialPerspective(float height, float cosChi)
{
    float R = _PlanetaryRadius;
    float h = height;
    float r = R + h;

    // distGrazing = sqrt(r^2 - R^2) + sqrt((R + H)^2 - R^2)
    float distGrazing = sqrt(r * r - _PlanetaryRadiusSquared) + _GrazingAngleAtmosphereExitDistance;
    float distExit    = IntersectAtmosphereFromInside(height, cosChi);

    float u = distExit * rcp(distGrazing);
    float v = MapQuadraticHeight(h);

    return float2(u, v);
}

// returns {height, cosChi}.
float2 UnmapAerialPerspective(float2 uv)
{
    float height = UnmapQuadraticHeight(uv.y);

    float R = _PlanetaryRadius;
    float h = height;
    float r = R + h;

    // distGrazing = sqrt(r^2 - R^2) + sqrt((R + H)^2 - R^2)
    float distGrazing = sqrt(r * r - _PlanetaryRadiusSquared) + _GrazingAngleAtmosphereExitDistance;
    float distExit    = uv.x * distGrazing;

    // Solve the equation in IntersectAtmosphereFromInside() for 'cosChi':
    //
    // t = -b + sqrt(b^2 - c)
    // t = -(r * cosChi) + sqrt((r * cosChi)^2 - c)
    //
    // (t + (r * cosChi))   = sqrt((r * cosChi)^2 - c)
    // (t + (r * cosChi))^2 = (r * cosChi)^2 - c
    //
    // t^2 + 2 * t * (r * cosChi) + (r * cosChi)^2 - (r * cosChi)^2 = -c
    // t^2 + 2 * t * (r * cosChi) = -c
    // 2 * t * (r * cosChi) = -(c + t^2)
    // r * cosChi = -0.5 * (c / t + t)
    // cosChi = -0.5 * (c / t + t) / r

    float c      = r * r - _AtmosphericRadiusSquared;
    float cosChi = -0.5 * (c * rcp(distExit) + distExit) * rcp(r);

    return float2(height, cosChi);
}

float3 SampleTransmittanceTexture(float height, float cosChi)
{
    float2 uv       = MapAerialPerspective(height, cosChi);
	float2 optDepth = SAMPLE_TEXTURE2D_LOD(_OpticalDepthTexture, s_linear_clamp_sampler, uv, 0).xy;

	// Compose the optical depth with extinction at the sea level.
	return TransmittanceFromOpticalDepth(optDepth.x * _AirSeaLevelExtinction +
										 optDepth.y * _AerosolSeaLevelExtinction);
}

float3 SampleGroundIrradianceTexture(float NdotL)
{
    // NdotL = 1 - 2 * u
    // u     = 0.5 - 0.5 * NdotL
    float2 uv = float2(0.5 - 0.5 * NdotL, 0);

    return SAMPLE_TEXTURE2D_LOD(_GroundIrradianceTexture, s_linear_clamp_sampler, uv, 0).rgb;
}