// Terrain shader (#124) — the heightfield terrain's material shader, bound per terrain through the engine's custom
// material-shader route (same root signature, cbuffers and texture slots as standard.hlsl; VSMain is identical so
// the standard shadow / SSAO passes keep working for the chunk meshes).
//
// Splat mapping with four PBR layers out of the six material slots:
//   t0 AlbedoTexture    the layers' albedo maps as a 2x2 atlas (layer i in quadrant (i & 1, i >> 1))
//   t1 NormalTexture    the layers' normal maps as the same 2x2 atlas
//   t2 MetallicTexture  the splat map: RGBA = the weights of layers 0..3 across the whole terrain (uv 0..1)
//   t3 RoughnessTexture the layers' roughness maps as the same 2x2 atlas (red channel)
//   PerObject.BaseColor the layers' tiling as tiles per metre (x..w = layers 0..3); the layer UVs come from the world
//                       XZ position, so neighbouring terrains and chunks tile seamlessly
// Lighting, shadows, fog and tone mapping are the standard PBR path (copied — the engine's HLSL has no includes).
// Conventions: row-major matrices, mul(vec, mat); the cbuffer layouts are ABI-coupled to the C++ structs.

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
    float4 BaseColor;        // terrain: tiles per metre of layers 0..3
    float Metallic;
    float Roughness;         // terrain: the roughness when no roughness atlas is bound
    float AO;
    float NormalStrength;
    uint HasAlbedoTexture;
    uint HasNormalTexture;
    uint HasMetallicTexture; // terrain: the splat map is bound
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

struct PointLight
{
    float3 position;
    float range;
    float3 color;
    float intensity;
};

struct SpotLight
{
    float3 position;
    float range;
    float3 direction;
    float spotAngle;
    float3 color;
    float intensity;
    float innerSpotAngle;
    float shadowStrength;
    float shadowBias;
    float shadowSlot;
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
    float4 SkySh[9];   // sky light (IBL step 1) @2304: SH9 irradiance per channel, w of [0] = on
};

Texture2D AlbedoTexture    : register(t0);
Texture2D NormalTexture    : register(t1);
Texture2D MetallicTexture  : register(t2);
Texture2D RoughnessTexture : register(t3);
Texture2D AOTexture        : register(t4);
Texture2D HeightTexture    : register(t6);
Texture2D ShadowMap        : register(t7);
Texture2D CsmShadowMap     : register(t8);
Texture2D PointShadowMap   : register(t9);
Texture2D SsaoTex          : register(t10);
SamplerState LinearSampler : register(s0);
SamplerComparisonState ShadowSampler : register(s1);
SamplerState ScreenSampler : register(s2);

// ---------------------------------------------------------------- fog (standard.hlsl)
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

// ---------------------------------------------------------------- shadows (standard.hlsl)
float SampleSpotShadow(float3 worldPos, int slot, float strength, float bias)
{
    float4 sp = mul(float4(worldPos, 1.0), ShadowVP[slot]);
    if (sp.w <= 0.0) return 1.0;
    float3 ndc = sp.xyz / sp.w;
    float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;
    suv = clamp(suv, ShadowMapTexel * 0.5, 1.0 - ShadowMapTexel * 0.5) * 0.5;
    suv += float2((slot & 1) != 0 ? 0.5 : 0.0, (slot & 2) != 0 ? 0.5 : 0.0);
    float lit = ShadowMap.SampleCmpLevelZero(ShadowSampler, suv, ndc.z - bias);
    return lerp(1.0, lit, saturate(strength));
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
        if (min(suv.x, suv.y) < 0.02 || max(suv.x, suv.y) > 0.98 || ndc.z > 1.0 || ndc.z < 0.0)
            continue;
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

float SamplePointShadow(float3 worldPos, float3 lightPos, int lightIndex)
{
    [unroll]
    for (int p = 0; p < 2; ++p)
    {
        if ((int)PointShadows[p].x != lightIndex) continue;
        float3 d = worldPos - lightPos;
        float3 ad = abs(d);
        int face;
        if (ad.x >= ad.y && ad.x >= ad.z) face = d.x > 0.0 ? 0 : 1;
        else if (ad.y >= ad.z)            face = d.y > 0.0 ? 2 : 3;
        else                              face = d.z > 0.0 ? 4 : 5;
        float4 sp = mul(float4(worldPos, 1.0), PointFaceVP[p * 6 + face]);
        if (sp.w <= 0.0) return 1.0;
        float3 ndc = sp.xyz / sp.w;
        float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;
        const float tileTexel = 1.0 / 1024.0;
        suv = clamp(suv, tileTexel * 1.5, 1.0 - tileTexel * 1.5);
        int tile = p * 6 + face;
        float2 auv = (suv + float2(tile & 3, tile >> 2)) * float2(0.25, 1.0 / 3.0);
        const float2 atlasTexel = float2(1.0 / 4096.0, 1.0 / 3072.0);
        float lit = 0.0;
        [unroll]
        for (int y = -1; y <= 1; ++y)
            [unroll]
            for (int x = -1; x <= 1; ++x)
                lit += PointShadowMap.SampleCmpLevelZero(ShadowSampler, auv + float2((float)x, (float)y) * atlasTexel, ndc.z - PointShadows[p].z);
        return lerp(1.0, lit / 9.0, saturate(PointShadows[p].y));
    }
    return 1.0;
}

// ---------------------------------------------------------------- vertex stage (identical to standard.hlsl)
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
    float3 N = output.norm;
    float3 c = cross(N, float3(0, 1, 0));
    float3 T = (dot(c, c) < 1e-6) ? normalize(cross(N, float3(1, 0, 0))) : normalize(c);
    float3 B = normalize(cross(N, T));
    output.tangent = T;
    output.bitangent = B;
    output.tint = tint;
    return output;
}

// ---------------------------------------------------------------- BRDF (standard.hlsl)
float D_GGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float d = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d + 0.0001);
}

float G_SchlickGGX(float NdotV, float roughness)
{
    float k = (roughness + 1.0);
    k = (k * k) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k + 0.0001);
}

float G_Smith(float NdotV, float NdotL, float roughness)
{
    return G_SchlickGGX(NdotV, roughness) * G_SchlickGGX(NdotL, roughness);
}

float3 F_Schlick(float VdotH, float3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);
}

float3 SRGBToLinear(float3 color)
{
    return pow(max(color, 0.0), 2.2);
}

float Attenuation(float distance, float range)
{
    float d = distance / range;
    float atten = saturate(1.0 - d * d);
    return atten * atten / (distance * distance + 0.01);
}

// ---------------------------------------------------------------- splat layers
// Atlas UV of layer i for the tiled coordinate uv: quadrant (i & 1, i >> 1), a small inset so the bilinear / mip
// footprint never bleeds into the neighbouring layer. The gradients come from the CONTINUOUS world coordinate (not
// from frac), so the mip level is right across every tile seam; they are clamped so the coarsest mip still keeps the
// four quadrants apart.
float2 AtlasUV(float2 uv, int layer)
{
    const float pad = 1.0 / 128.0;
    float2 q = float2((float)(layer & 1), (float)(layer >> 1));
    return (q + pad + frac(uv) * (1.0 - 2.0 * pad)) * 0.5;
}

void ClampGrad(inout float2 dx, inout float2 dy)
{
    const float maxG = 1.0 / 16.0;   // atlas UV per pixel: never coarser than 8 texels per quadrant on a 256² layer
    float gl = max(length(dx), length(dy));
    if (gl > maxG) { float s = maxG / gl; dx *= s; dy *= s; }
}

// Sky light (IBL step 1): the diffuse radiance a surface facing n receives from the sky, from the SH9 irradiance baked
// off the scene's sky picture — E(n)/π with the cosine-lobe weights (1, 2/3, 1/4 per band). Normalised by the baker to
// the old hemisphere level, so AmbientStrength keeps its meaning and the picture adds colour and direction.
float3 SkyIrradiance(float3 n)
{
    float x = n.x, y = n.y, z = n.z;
    float3 e = SkySh[0].rgb * 0.282095;
    e += (2.0 / 3.0) * (SkySh[1].rgb * (0.488603 * y) + SkySh[2].rgb * (0.488603 * z) + SkySh[3].rgb * (0.488603 * x));
    e += 0.25 * (SkySh[4].rgb * (1.092548 * x * y) + SkySh[5].rgb * (1.092548 * y * z) + SkySh[6].rgb * (0.315392 * (3.0 * z * z - 1.0))
               + SkySh[7].rgb * (1.092548 * x * z) + SkySh[8].rgb * (0.546274 * (x * x - y * y)));
    return max(e, 0.0);
}

// Macro variation (#124 follow-up): a 2-octave value noise over the world XZ at 40 m / 9 m that scales the blended albedo
// by ±9 %, so a layer's repeats stop reading as a grid across a 500 m terrain — no extra texture samples.
float MacroHash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float MacroNoise(float2 p)
{
    float2 i = floor(p), f = frac(p); f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(MacroHash(i), MacroHash(i + float2(1, 0)), f.x), lerp(MacroHash(i + float2(0, 1)), MacroHash(i + float2(1, 1)), f.x), f.y);
}
float MacroVariation(float2 worldXZ)
{
    float n = 0.65 * MacroNoise(worldXZ * 0.025) + 0.35 * MacroNoise(worldXZ * 0.11 + 7.3);
    return 0.91 + 0.18 * n;
}

float4 PSMain(PS_IN input) : SV_TARGET
{
    float3 Ng = normalize(input.norm);

    // layer weights from the splat map (uv 0..1 across the terrain); no splat map = layer 0 everywhere
    float4 w = HasMetallicTexture != 0 ? MetallicTexture.Sample(LinearSampler, input.uv) : float4(1.0, 0.0, 0.0, 0.0);
    float wsum = w.r + w.g + w.b + w.a;
    w = wsum > 1e-4 ? w / wsum : float4(1.0, 0.0, 0.0, 0.0);

    // world-space tiling: layer i repeats every 1 / BaseColor[i] metres
    float2 p = input.worldPos.xz;
    float4 tilesPerMetre = max(BaseColor, 1e-4);
    float2 dpx = ddx(p), dpy = ddy(p);   // outside any branch: derivatives

    float3 albedo = float3(0.0, 0.0, 0.0);
    float3 nm = float3(0.0, 0.0, 0.0);
    float rough = 0.0;
    // detail: the layer's own normal map again at 7.3x finer tiling, fading out beyond ~28 m — close-up ripples and grain
    float detailW = saturate(1.0 - length(CameraPosition - input.worldPos) / 28.0);
    [unroll]
    for (int i = 0; i < 4; ++i)
    {
        float wi = w[i];
        if (wi <= 0.002) continue;
        float2 uv = p * tilesPerMetre[i];
        float2 a = AtlasUV(uv, i);
        float2 dx = dpx * tilesPerMetre[i] * 0.5, dy = dpy * tilesPerMetre[i] * 0.5;
        ClampGrad(dx, dy);
        albedo += wi * (HasAlbedoTexture != 0 ? SRGBToLinear(AlbedoTexture.SampleGrad(LinearSampler, a, dx, dy).rgb) : float3(0.5, 0.5, 0.5));
        if (HasNormalTexture != 0) nm += wi * (NormalTexture.SampleGrad(LinearSampler, a, dx, dy).rgb * 2.0 - 1.0);
        if (HasNormalTexture != 0 && detailW > 0.001)
        {
            float2 ad = AtlasUV(uv * 7.3 + 0.37, i);
            float2 dxd = dx * 7.3, dyd = dy * 7.3;
            ClampGrad(dxd, dyd);
            float3 nd = NormalTexture.SampleGrad(LinearSampler, ad, dxd, dyd).rgb * 2.0 - 1.0;
            nm += wi * detailW * 0.6 * float3(nd.xy, 0.0);
        }
        if (HasRoughnessTexture != 0) rough += wi * RoughnessTexture.SampleGrad(LinearSampler, a, dx, dy).r;
    }
    albedo *= input.tint.rgb;
    albedo *= MacroVariation(input.worldPos.xz);
    float roughness = max(HasRoughnessTexture != 0 ? rough : Roughness, 0.04);
    float metallic = 0.0;
    float ao = AO;

    // normal mapping in the world-aligned frame of the tiled UVs: T follows +X (+u), B follows +Z (+v)
    float3 N = Ng;
    if (HasNormalTexture != 0 && dot(nm, nm) > 1e-6)
    {
        if (UseDirectXNormals == 0) nm.y = -nm.y;
        nm.xy *= NormalStrength;
        float3 T = normalize(float3(1.0, 0.0, 0.0) - Ng * Ng.x);
        float3 B = normalize(float3(0.0, 0.0, 1.0) - Ng * Ng.z);
        N = normalize(nm.x * T + nm.y * B + max(nm.z, 0.05) * Ng);
    }

    float3 V = normalize(CameraPosition - input.worldPos);
    float NdotV = max(dot(N, V), 0.001);
    float3 F0 = float3(0.04, 0.04, 0.04);
    float3 Lo = float3(0, 0, 0);

    if (DirectionalIntensity > 0.001)
    {
        float3 L = normalize(-LightDirection);
        float3 H = normalize(V + L);
        float NdotL = max(dot(N, L), 0.0);
        float NdotH = max(dot(N, H), 0.0);
        float VdotH = max(dot(V, H), 0.0);
        float D = D_GGX(NdotH, roughness);
        float G = G_Smith(NdotV, NdotL, roughness);
        float3 F = F_Schlick(VdotH, F0);
        float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
        float3 kD = (1.0 - F) * (1.0 - metallic);
        float3 radiance = LightColor * DirectionalIntensity;
        radiance *= SampleCascadeShadow(input.worldPos);
        Lo += (kD * albedo / PI + spec) * radiance * NdotL;
    }

    for (uint li = 0; li < PointLightCount && li < MAX_POINT_LIGHTS; ++li)
    {
        float3 lightVec = PointLights[li].position - input.worldPos;
        float dist = length(lightVec);
        if (dist < PointLights[li].range)
        {
            float3 L = lightVec / dist;
            float3 H = normalize(V + L);
            float NdotL = max(dot(N, L), 0.0);
            float NdotH = max(dot(N, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);
            float atten = Attenuation(dist, PointLights[li].range);
            float3 radiance = PointLights[li].color * PointLights[li].intensity * atten;
            radiance *= SamplePointShadow(input.worldPos, PointLights[li].position, (int)li);
            float D = D_GGX(NdotH, roughness);
            float G = G_Smith(NdotV, NdotL, roughness);
            float3 F = F_Schlick(VdotH, F0);
            float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
            float3 kD = (1.0 - F) * (1.0 - metallic);
            Lo += (kD * albedo / PI + spec) * radiance * NdotL;
        }
    }

    for (uint j = 0; j < SpotLightCount && j < MAX_SPOT_LIGHTS; ++j)
    {
        float3 lightVec = SpotLights[j].position - input.worldPos;
        float dist = length(lightVec);
        if (dist < SpotLights[j].range)
        {
            float3 L = lightVec / dist;
            float3 spotDir = normalize(SpotLights[j].direction);
            float theta = dot(-L, spotDir);
            float outerCos = cos(radians(SpotLights[j].spotAngle * 0.5));
            float innerCos = cos(radians(SpotLights[j].innerSpotAngle * 0.5));
            float spotFade = saturate((theta - outerCos) / (innerCos - outerCos + 0.001));
            if (theta > outerCos)
            {
                float3 H = normalize(V + L);
                float NdotL = max(dot(N, L), 0.0);
                float NdotH = max(dot(N, H), 0.0);
                float VdotH = max(dot(V, H), 0.0);
                float atten = Attenuation(dist, SpotLights[j].range) * spotFade;
                float3 radiance = SpotLights[j].color * SpotLights[j].intensity * atten;
                if (SpotLights[j].shadowSlot >= 0.0)
                    radiance *= SampleSpotShadow(input.worldPos, (int)SpotLights[j].shadowSlot, SpotLights[j].shadowStrength, SpotLights[j].shadowBias);
                float D = D_GGX(NdotH, roughness);
                float G = G_Smith(NdotV, NdotL, roughness);
                float3 F = F_Schlick(VdotH, F0);
                float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
                float3 kD = (1.0 - F) * (1.0 - metallic);
                Lo += (kD * albedo / PI + spec) * radiance * NdotL;
            }
        }
    }

    // ambient: hemisphere + the sky gradient's reflection, darkened by SSAO
    float3 skyColor = float3(0.5, 0.55, 0.7);
    float3 groundColor = float3(0.15, 0.15, 0.18);
    float skyAmount = dot(N, float3(0, 1, 0)) * 0.5 + 0.5;
    float3 hemisphereLight = lerp(groundColor, skyColor, skyAmount);
    if (SkySh[0].w > 0.5) hemisphereLight = SkyIrradiance(N);   // the real sky: blue from above, the ground's bounce from below
    float3 ambient = hemisphereLight * AmbientStrength * albedo * ao;

    float3 R = reflect(-V, N);
    float3 envColor;
    if (EnvSky.w > 0.5)
    {
        float3 skyDir = R.y >= 0.0 ? lerp(EnvHorizon.rgb, EnvSky.rgb, pow(saturate(R.y), 0.6))
                                   : lerp(EnvHorizon.rgb, EnvGround.rgb, pow(saturate(-R.y), 0.6));
        float3 skyAvg = (EnvSky.rgb + 2.0 * EnvHorizon.rgb + EnvGround.rgb) * 0.25;
        envColor = lerp(skyDir, skyAvg, saturate(roughness * roughness * 1.5)) * AmbientStrength;
    }
    else
    {
        float upFactor = R.y * 0.5 + 0.5;
        envColor = lerp(float3(0.01, 0.01, 0.02), float3(0.08, 0.10, 0.15), upFactor);
        envColor = lerp(envColor, envColor * 0.2, roughness * roughness);
    }
    float3 envFresnel = F0 + (max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), F0) - F0) * pow(1.0 - NdotV, 5.0);
    ambient += envColor * envFresnel * ao;

    if (SsaoEnabled > 0.5)
    {
        float aoW, aoH;
        SsaoTex.GetDimensions(aoW, aoH);
        float2 aoUV = input.pos.xy / float2(aoW * 2.0, aoH * 2.0);
        ambient *= SsaoTex.Sample(ScreenSampler, aoUV).r;
    }

    float3 color = ambient + Lo;
    color = ApplyFog(color, input.worldPos);

    float3 x = color * 0.5;
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    color = saturate(a / b);
    color = pow(max(color, 0.0), 1.0 / 2.2);
    return float4(color, 1.0);
}
