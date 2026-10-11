// Water shader (#200) — lakes, ponds, pools: bound per Water component through the custom material-shader route
// (standard cbuffers; alpha-blended, so it draws in the sorted transparent pass after the opaque world).
//
// The surface mesh (Editor/Core/Services/Water/WaterService.cs) carries the depth of the ground below every vertex in
// uv.x, so the shader needs no scene depth: the colour darkens with depth (shallow -> deep), the surface fades out at the
// shore, foam runs along the bank. Ripples are analytic sine waves (no textures); the sky reflects through Fresnel from
// the scene's sky gradient, the sun glitters by roughness and casts its cascaded shadows onto the surface.
// Per-material inputs, in fields the standard material never uses for a water surface:
//   BaseColor.rgb deep colour, BaseColor.a reflection strength, instance tint.rgb shallow colour,
//   NormalStrength absorption depth (m), UVTiling (wave scale m, wave speed), AlphaCutoff wave height, HeightScale time (s),
//   EmissiveStrength foam width (m), Roughness roughness.

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

cbuffer PerFrame : register(b0)
{
    row_major float4x4 ViewProjection;
    float3 CameraPosition;
    float Padding0;
    float3 LightDirection;
    float DirectionalIntensity;
    float3 LightColor;
    float AmbientStrength;
    uint PointLightCount;
    uint SpotLightCount;
    uint2 FramePadding;
    float3 FogColor;
    float FogDensity;
    float FogHeightY;
    float FogHeightFalloff;
    uint FogMode;
    float FogPadding;
    float ShadowMapTexel;
    uint3 ShadowPadding;
    float SsaoEnabled;
    float3 SsaoPadding;
    float4 EnvSky;
    float4 EnvHorizon;
    float4 EnvGround;
};

cbuffer PerObject : register(b1)
{
    row_major float4x4 World;
    float4 BaseColor;
    float Metallic;
    float Roughness;
    float AO;
    float NormalStrength;
    uint HasAlbedoTexture;
    uint HasNormalTexture;
    uint HasMetallicTexture;
    uint HasRoughnessTexture;
    uint HasAOTexture;
    uint UseDirectXNormals;
    uint IsUnlit;
    float EmissiveStrength;
    float2 UVTiling;
    uint HasHeightTexture;
    float HeightScale;
    float AlphaCutoff;
    float3 _Pad0;
};

struct PointLight { float3 position; float range; float3 color; float intensity; };
struct SpotLight
{
    float3 position; float range; float3 direction; float spotAngle; float3 color; float intensity;
    float innerSpotAngle; float shadowStrength; float shadowBias; float shadowSlot;
};

cbuffer LightBuffer : register(b2)
{
    PointLight PointLights[MAX_POINT_LIGHTS];
    SpotLight SpotLights[MAX_SPOT_LIGHTS];
    row_major float4x4 ShadowVP[4];
    row_major float4x4 CascadeVP[3];
    float4 CascadeSplits;
    float4 DirShadowParams;
    float4 PointShadows[2];
    row_major float4x4 PointFaceVP[12];
};

Texture2D CsmShadowMap     : register(t8);
Texture2D EnvMap           : register(t11);   // the sky as a reflection (IBL step 2)
SamplerComparisonState ShadowSampler : register(s1);

float FogOpticalDepth(float density, float heightY, float k, float3 cam, float3 worldPos)
{
    float3 delta = worldPos - cam;
    float dist = length(delta);
    if (k <= 0.0) { float d = density * dist; return -log2(max(exp2(-d * d), 1e-6)); }
    float ya = cam.y - heightY, yb = worldPos.y - heightY;
    if (ya <= 0.0 && yb <= 0.0) return density * dist;
    float t0 = 0.0, t1 = 1.0;
    if (ya <= 0.0) t0 = -ya / (yb - ya);
    else if (yb <= 0.0) t1 = ya / (ya - yb);
    float above = (t1 - t0) * dist, below = dist - above;
    float hya = max(ya, 0.0), hyb = max(yb, 0.0), dyv = hyb - hya;
    float meanDensity = abs(dyv) > 1e-3 ? (exp(-k * hya) - exp(-k * hyb)) / (k * dyv) : exp(-k * hya);
    return density * (below + above * meanDensity);
}

float3 ApplyFog(float3 color, float3 worldPos)
{
    if (FogDensity <= 0.0) return color;
    float optical = FogOpticalDepth(FogDensity, FogHeightY, FogHeightFalloff, CameraPosition, worldPos);
    float f = 1.0 - exp2(-optical);
    return lerp(color, FogColor, saturate(f));
}

float SampleCascadeShadow(float3 worldPos)
{
    int count = (int)DirShadowParams.z;
    if (count <= 0) return 1.0;
    [unroll]
    for (int c = 0; c < 3; ++c)
    {
        if (c >= count) break;
        float4 sp = mul(float4(worldPos, 1.0), CascadeVP[c]);
        if (sp.w <= 0.0) continue;
        float3 ndc = sp.xyz / sp.w;
        float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (min(suv.x, suv.y) < 0.02 || max(suv.x, suv.y) > 0.98 || ndc.z > 1.0 || ndc.z < 0.0) continue;
        suv = clamp(suv, ShadowMapTexel * 1.5, 1.0 - ShadowMapTexel * 1.5) * 0.5;
        suv += float2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
        float bias = DirShadowParams.y * (1.0 + (float)c);
        float atlasTexel = ShadowMapTexel * 0.5;
        float lit = 0.0;
        [unroll]
        for (int y = -1; y <= 1; ++y)
            [unroll]
            for (int x = -1; x <= 1; ++x)
                lit += CsmShadowMap.SampleCmpLevelZero(ShadowSampler, suv + float2((float)x, (float)y) * atlasTexel, ndc.z - bias);
        return lerp(1.0, lit / 9.0, saturate(DirShadowParams.x));
    }
    return 1.0;
}

struct VS_IN
{
    float3 pos  : POSITION;
    float3 norm : NORMAL;
    float2 uv   : TEXCOORD0;
    float4 iw0 : INSTANCEWORLD0;
    float4 iw1 : INSTANCEWORLD1;
    float4 iw2 : INSTANCEWORLD2;
    float4 iw3 : INSTANCEWORLD3;
};

struct PS_IN
{
    float4 pos       : SV_POSITION;
    float3 worldPos  : TEXCOORD1;
    float3 norm      : TEXCOORD2;
    float2 uv        : TEXCOORD0;
    float3 tangent   : TEXCOORD3;
    float3 bitangent : TEXCOORD4;
    float4 tint      : COLOR0;
};

PS_IN VSMain(VS_IN input)
{
    PS_IN output;
    float4 tint = float4(1.0 + input.iw0.w, 1.0 + input.iw1.w, 1.0 + input.iw2.w, input.iw3.w);
    float4x4 World = float4x4(float4(input.iw0.xyz, 0), float4(input.iw1.xyz, 0), float4(input.iw2.xyz, 0), float4(input.iw3.xyz, 1));
    float4 worldPos = mul(float4(input.pos, 1), World);
    output.worldPos = worldPos.xyz;
    output.pos = mul(worldPos, ViewProjection);
    output.norm = normalize(mul(input.norm, (float3x3)World));
    output.uv = input.uv;
    output.tangent = float3(1, 0, 0);
    output.bitangent = float3(0, 0, 1);
    output.tint = tint;
    return output;
}

// ---------------------------------------------------------------- waves
// Three directional sine waves plus a fine ripple; the normal comes from the analytic slope.
float3 WaveNormal(float2 p, float t, float height)
{
    float2 d1 = normalize(float2(1.0, 0.6)), d2 = normalize(float2(-0.7, 1.0)), d3 = normalize(float2(0.3, -1.0)), d4 = normalize(float2(-1.0, -0.4));
    float a1 = 0.55, a2 = 0.35, a3 = 0.22, a4 = 0.10;
    float f1 = 1.0, f2 = 1.7, f3 = 2.9, f4 = 7.3;
    float s1 = 0.9, s2 = 1.3, s3 = 1.9, s4 = 3.1;
    float c1 = cos(dot(d1, p) * f1 + t * s1), c2 = cos(dot(d2, p) * f2 + t * s2), c3 = cos(dot(d3, p) * f3 + t * s3), c4 = cos(dot(d4, p) * f4 + t * s4);
    float dx = a1 * f1 * d1.x * c1 + a2 * f2 * d2.x * c2 + a3 * f3 * d3.x * c3 + a4 * f4 * d4.x * c4;
    float dz = a1 * f1 * d1.y * c1 + a2 * f2 * d2.y * c2 + a3 * f3 * d3.y * c3 + a4 * f4 * d4.y * c4;
    return normalize(float3(-dx * height, 1.0, -dz * height));
}

float Hash2(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float Noise2(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(Hash2(i), Hash2(i + float2(1, 0)), f.x), lerp(Hash2(i + float2(0, 1)), Hash2(i + float2(1, 1)), f.x), f.y);
}

// The sky as a reflection (IBL step 2): an equirect of the sky with mips at t11, EnvSky.w = 2 while bound, EnvHorizon.w =
// its mip count. Radiance was soft-compressed into 8 bits by the baker (straight below 1, x / (1 + x) above) — undone here.
float2 EnvUV(float3 d)
{
    return float2(atan2(d.x, d.z) / (2.0 * 3.14159265) + 0.5, acos(clamp(d.y, -1.0, 1.0)) / 3.14159265);
}
float3 DecodeEnv(float3 c)
{
    float3 lo = c / 0.75;
    float3 t = saturate((c - 0.75) / 0.25);
    float3 hi = 1.0 / max(1.0 - t, 0.0625);
    return c.x <= 0.75 && c.y <= 0.75 && c.z <= 0.75 ? lo : max(lo, hi);
}
float3 SampleEnv(float3 R, float roughness, float lodMin)
{
    float lod = max(saturate(roughness) * max(EnvHorizon.w - 1.0, 0.0), lodMin);
    return DecodeEnv(EnvMap.SampleLevel(LinearSampler, EnvUV(R), lod).rgb);
}

float4 PSMain(PS_IN input) : SV_TARGET
{
    float depth = max(input.uv.x, 0.0);
    float3 deep = BaseColor.rgb, shallow = input.tint.rgb;
    float reflection = saturate(BaseColor.a);
    float absorb = max(NormalStrength, 0.1);
    float waveScale = max(UVTiling.x, 0.1), waveSpeed = max(UVTiling.y, 0.0);
    float t = HeightScale * waveSpeed;
    float foamWidth = EmissiveStrength;
    float rough = clamp(Roughness, 0.01, 1.0);

    float2 p = input.worldPos.xz / waveScale;
    float3 N = WaveNormal(p, t, AlphaCutoff);
    // the ripples fade with distance: beyond ~80 m the per-pixel sine slopes only alias into stripes
    float camDist = length(CameraPosition - input.worldPos);
    N = normalize(lerp(float3(0.0, 1.0, 0.0), N, saturate(80.0 / max(camDist, 1.0))));
    float3 V = normalize(CameraPosition - input.worldPos);
    float NdotV = max(dot(N, V), 0.0);
    float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

    // the body of water: shallow -> deep with depth (Beer-Lambert)
    float body = 1.0 - exp(-depth / absorb);
    float3 water = lerp(shallow, deep, body);

    // light on the body: hemisphere ambient + a little sun
    float3 skyColor = EnvSky.w > 0.5 ? EnvSky.rgb : float3(0.5, 0.55, 0.7);
    float3 horizon = EnvSky.w > 0.5 ? EnvHorizon.rgb : float3(0.6, 0.62, 0.68);
    float3 L = normalize(-LightDirection);
    float shadow = SampleCascadeShadow(input.worldPos);
    float3 sun = LightColor * DirectionalIntensity * shadow;
    float3 lit = water * (horizon * AmbientStrength * 1.2 + sun * (0.12 + 0.28 * saturate(dot(N, L))));

    // the sky in the surface
    float3 R = reflect(-V, N);
    float3 skyRefl;
    if (EnvSky.w > 1.5)
    {
        // the real sky in the water: never below the horizon (the picture's ground is not the lake's surroundings), and
        // blurred with distance — far away the per-pixel ripple slopes would only turn the sky's sharp horizon into stripes
        float lodMin = 4.0 * saturate((camDist - 40.0) / 160.0);
        skyRefl = SampleEnv(float3(R.x, max(R.y, 0.02), R.z), rough, lodMin);
    }
    else if (EnvSky.w > 0.5)
        skyRefl = R.y >= 0.0 ? lerp(EnvHorizon.rgb, EnvSky.rgb, pow(saturate(R.y), 0.5)) : lerp(EnvHorizon.rgb, EnvGround.rgb, pow(saturate(-R.y), 0.6));
    else
        skyRefl = lerp(horizon, skyColor, saturate(R.y)) * 0.8;
    skyRefl *= max(AmbientStrength, 0.3) * 1.3;
    float3 color = lerp(lit, skyRefl, fresnel * reflection);

    // sun glitter
    float3 H = normalize(V + L);
    float specPow = lerp(1400.0, 24.0, rough);
    float spec = pow(max(dot(N, H), 0.0), specPow) * (specPow + 8.0) / (8.0 * PI) * 0.06;
    color += sun * spec * lerp(0.4, 1.0, fresnel);

    // foam along the bank, and a few streaks on the open water
    float foam = 0.0;
    if (foamWidth > 0.0)
    {
        float n = Noise2(p * 6.0 + float2(t * 0.35, -t * 0.2)) * 0.6 + Noise2(p * 17.0 - float2(t * 0.5, t * 0.3)) * 0.4;
        float band = saturate(1.0 - depth / foamWidth);
        foam = saturate(band * band * (0.35 + 0.9 * n) * 1.4);
    }
    float3 foamColor = float3(0.92, 0.94, 0.92) * (horizon * AmbientStrength + sun * 0.9);
    color = lerp(color, foamColor, foam * 0.85);

    // the surface is clearer near the shore, nearly opaque over deep water
    float alpha = saturate(depth / 0.3) * lerp(0.5, 0.97, saturate(depth / absorb));
    alpha = max(alpha, foam * 0.95 * saturate(depth / 0.08));

    color = ApplyFog(color, input.worldPos);
    float3 x = color * 0.5;
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    color = saturate(a / b);
    color = pow(max(color, 0.0), 1.0 / 2.2);
    return float4(color, alpha);
}
