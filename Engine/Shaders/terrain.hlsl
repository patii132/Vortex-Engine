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
    float SceneExposure;   // pre-tonemap multiplier (0.5 = the classic look; <= 0 reads as 0.5)
    float ShadowMapTexel;
    uint Tonemapper;       // 0 = ACES fit, 1 = PBR Neutral
    float GiStrength;      // screen-space GI: > 0 = SsaoTex.rgb carries bounce light
    uint ShadowPadding1;
    float SsaoEnabled;
    float2 SsaoPadding;
    float FogSunGlow;      // aerial perspective: the sun's glow in the fog (0 = off)
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
Texture2D EnvMap           : register(t11);   // the sky as a reflection (IBL step 2)
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

// The scene's tonemapper (Tonemapper in the frame constants): 0 = the ACES fit — the classic look, which pulls bright
// saturated colours towards white; 1 = Khronos PBR Neutral — hue-preserving with a gentle highlight desaturation, so a
// sunlit desert stays orange and a red car stays red. The input is the EXPOSED scene colour (scene exposure applied).
float3 TonemapNeutral(float3 color)
{
    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;
    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    color -= offset;
    float peak = max(color.r, max(color.g, color.b));
    if (peak < startCompression) return color;
    const float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);
    color *= newPeak / peak;
    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return lerp(color, newPeak * float3(1.0, 1.0, 1.0), g);
}
float3 TonemapScene(float3 x, uint mode)
{
    if (mode == 1) return saturate(TonemapNeutral(max(x, 0.0)));
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    return saturate(a / b);
}

// Aerial perspective: what the fog scatters towards the eye. The flat FogColor, plus the sun's forward-scatter glow when
// looking towards it (FogSunGlow 0 = off) — the haze brightens and warms around the sun like real air does.
float3 FogInscatter(float3 worldPos)
{
    float3 c = FogColor;
    if (FogSunGlow > 0.0 && DirectionalIntensity > 0.001)
    {
        float3 V = normalize(worldPos - CameraPosition);
        float mu = saturate(dot(V, normalize(-LightDirection)));
        float phase = 0.12 * mu * mu + 0.88 * pow(mu, 10.0);
        c += LightColor * (FogSunGlow * phase);
    }
    return c;
}

float3 ApplyFog(float3 color, float3 worldPos)
{
    if (FogDensity <= 0.0) return color;
    float optical = FogOpticalDepth(FogDensity, FogHeightY, FogHeightFalloff, CameraPosition, worldPos);
    float f = 1.0 - exp2(-optical);
    return lerp(color, FogInscatter(worldPos), saturate(f));
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

// The sun's shadow filter (Light ▸ Soft): softness <= 1 = the classic 3x3 box; above it a 16-tap Poisson disk of that
// radius in shadow texels — palm shadows on sand get a real penumbra instead of a stair-stepped edge.
static const float2 PoissonDisk16[16] = { float2(-0.94201624,-0.39906216), float2(0.94558609,-0.76890725), float2(-0.094184101,-0.92938870), float2(0.34495938,0.29387760), float2(-0.91588581,0.45771432), float2(-0.81544232,-0.87912464), float2(-0.38277543,0.27676845), float2(0.97484398,0.75648379), float2(0.44323325,-0.97511554), float2(0.53742981,-0.47373420), float2(-0.26496911,-0.41893023), float2(0.79197514,0.19090188), float2(-0.24188840,0.99706507), float2(-0.81409955,0.91437590), float2(0.19984126,0.78641367), float2(0.14383161,-0.14100790) };
float CsmPcf(float2 suv, float depth, float atlasTexel, float softness, float zscale, float mpt)
{
    float lit = 0.0;
    if (softness <= 1.01)
    {
        [unroll]
        for (int y = -1; y <= 1; ++y)
            [unroll]
            for (int x = -1; x <= 1; ++x)
                lit += CsmShadowMap.SampleCmpLevelZero(ShadowSampler, suv + float2((float)x, (float)y) * atlasTexel, depth);
        return lit / 9.0;
    }
    // contact hardening (PCSS without a depth read): comparison taps at the receiver's depth minus 0 / 1 / 3 / 9 m over the
    // widest penumbra tell how far behind the receiver the blockers sit; the Poisson radius grows with that distance — a
    // trunk's shadow is sharp at its foot and soft where the crown throws it 10 m up. Softness caps the radius (texels).
    const float tan_a = 0.035;   // the sun's angular radius, widened to 2 degrees as the big engines do
    float rs = clamp(12.0 * tan_a / mpt, 1.0, softness) * atlasTexel;
    float sh[4];
    [unroll]
    for (int l = 0; l < 4; ++l)
    {
        float k = (l == 0) ? 0.0 : (l == 1 ? 1.0 : (l == 2 ? 3.0 : 9.0));
        float s = 0.0;
        [unroll]
        for (int i = 0; i < 4; ++i) s += CsmShadowMap.SampleCmpLevelZero(ShadowSampler, suv + PoissonDisk16[i * 4 + l] * rs, depth - k * zscale);
        sh[l] = 1.0 - s * 0.25;
    }
    if (sh[0] < 0.02) return 1.0;   // nothing in the way anywhere in the search disk
    float bd = (0.5 * (sh[0] + sh[1]) + (sh[1] + sh[2]) + 3.0 * (sh[2] + sh[3])) / max(sh[0], 0.05);   // the mean blocker distance (m)
    float r = clamp(bd * tan_a / mpt, 1.0, softness) * atlasTexel;
    [unroll]
    for (int i = 0; i < 16; ++i)
        lit += CsmShadowMap.SampleCmpLevelZero(ShadowSampler, suv + PoissonDisk16[i] * r, depth);
    return lit / 16.0;
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
        float guard = ShadowMapTexel * (max(DirShadowParams.w, 1.0) + 0.5);   // the whole filter stays inside the tile
        suv = clamp(suv, guard, 1.0 - guard) * 0.5;
        suv += float2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
        float bias = DirShadowParams.y * (1.0 + (float)c) * (0.75 + 0.25 * max(DirShadowParams.w, 1.0));   // a wider filter needs more bias on slopes
        float atlasTexel = ShadowMapTexel * 0.5;
        float4x4 M = CascadeVP[c];
        float zscale = length(float3(M[0][2], M[1][2], M[2][2]));                       // depth units per metre along the light
        float mpt = 2.0 * ShadowMapTexel / max(length(float3(M[0][0], M[1][0], M[2][0])), 1e-6);   // metres per shadow texel in this cascade
        return lerp(1.0, CsmPcf(suv, ndc.z - bias, atlasTexel, DirShadowParams.w, zscale, mpt), saturate(DirShadowParams.x));
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
    const float pad = 1.0 / 32.0;    // the atlas builder fills this gutter with the WRAPPED texture, so every mip up to the cap is seamless
    float2 q = float2((float)(layer & 1), (float)(layer >> 1));
    return (q + pad + frac(uv) * (1.0 - 2.0 * pad)) * 0.5;
}

void ClampGrad(inout float2 dx, inout float2 dy)
{
    const float maxG = 1.0 / 32.0;   // atlas UV per pixel: never a mip whose gutter is under half a texel (quadrants would bleed)
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
// Sand ripples (procedural): crests across the north-east wind, 14 cm apart, bent by noise, in patches — a long gentle
// stoss slope towards the wind and a short steep lee; their tilt joins the normal within 45 m of the camera.
float2 SandRipples(float2 p, float camDist, float sandW)
{
    float fade = saturate(1.0 - camDist / 45.0) * sandW;
    if (fade < 0.002) return float2(0.0, 0.0);
    const float2 wind = float2(0.59, 0.81);
    float warp = MacroNoise(p * 0.9) * 2.0 - 1.0;
    float patches = saturate(MacroNoise(p * 0.07 + 3.1) * 1.6 - 0.3);
    float phase = (dot(p, wind) + 0.08 * warp) / 0.14;
    float aa = saturate(1.0 - fwidth(phase) * 1.5);   // no shimmer where a ripple is thinner than a pixel
    float f = frac(phase);
    float slope = f < 0.7 ? 0.35 : -0.82;
    slope *= smoothstep(0.0, 0.06, abs(f - 0.7)) * smoothstep(0.0, 0.04, min(f, 1.0 - f));   // rounded crests and troughs
    return -slope * 0.09 * patches * fade * aa * wind;
}
// The layer's mean colour: one texel of the quadrant two mips above the smallest.
float3 LayerMean(int k)
{
    uint w, h, mips; AlbedoTexture.GetDimensions(0, w, h, mips);
    return SRGBToLinear(AlbedoTexture.SampleLevel(LinearSampler, float2((float)(k & 1) + 0.5, (float)(k >> 1) + 0.5) * 0.5, max((float)mips - 2.0, 0.0)).rgb);
}

float MacroVariation(float2 worldXZ)
{
    float n = 0.65 * MacroNoise(worldXZ * 0.025) + 0.35 * MacroNoise(worldXZ * 0.11 + 7.3);
    return 0.86 + 0.28 * n;
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
float3 SampleEnv(float3 R, float roughness)
{
    float lod = saturate(roughness) * max(EnvHorizon.w - 1.0, 0.0);
    return DecodeEnv(EnvMap.SampleLevel(LinearSampler, EnvUV(R), lod).rgb);
}

// a normal-atlas texel: tangent-space xyz; a two-channel (BC5) atlas carries x / y and z is rebuilt per sample
float3 DecodeN(float3 s)
{
    s = s * 2.0 - 1.0;
    if (HasNormalTexture == 2) s.z = sqrt(saturate(1.0 - dot(s.xy, s.xy)));
    return s;
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
    float camDist = length(CameraPosition - input.worldPos);
    float detailW = saturate(1.0 - camDist / 70.0);
    // far: beyond ~250 m a tile is below the pixel — blend to the layer's mean colour and a flat normal (the 2x2 mip holds
    // exactly one texel per quadrant); the capped mip would otherwise shimmer with every camera move
    float farW = saturate((camDist - 260.0) / 640.0);
    [unroll]
    for (int i = 0; i < 4; ++i)
    {
        float wi = w[i];
        if (wi <= 0.002) continue;
        float2 uv = p * tilesPerMetre[i];
        float2 a = AtlasUV(uv, i);
        float2 dx = dpx * tilesPerMetre[i] * 0.5, dy = dpy * tilesPerMetre[i] * 0.5;
        ClampGrad(dx, dy);
        // the same layer at a 4.3x larger, offset tiling blended in half: the two periods never line up, so the 8 m repeat
        // that an aerial view shows as a grid of blotches dissolves (two samples per map instead of one)
        float2 aM = AtlasUV(uv * 0.23 + float2(0.21, 0.63), i);
        float2 dxM = dx * 0.23, dyM = dy * 0.23;
        ClampGrad(dxM, dyM);
        // the sharp sample keeps every texel; the 4.3x larger tiling only MODULATES its tone (sampled soft, three mips down)
        // and adds a little of its slope — averaging the two halved the grain's contrast and made the sand look blurred
        if (HasAlbedoTexture != 0)
        {
            float3 sharp = SRGBToLinear(AlbedoTexture.SampleGrad(LinearSampler, a, dx, dy).rgb);
            float3 broad = SRGBToLinear(AlbedoTexture.SampleGrad(LinearSampler, aM, dxM * 8.0, dyM * 8.0).rgb);
            float lumB = dot(broad, float3(0.299, 0.587, 0.114));
            float lumM = dot(LayerMean(i), float3(0.299, 0.587, 0.114));
            albedo += wi * sharp * lerp(1.0, lumB / max(lumM, 0.02), 0.45);
        }
        else albedo += wi * float3(0.5, 0.5, 0.5);
        if (HasNormalTexture != 0)
        {
            float3 ns = DecodeN(NormalTexture.SampleGrad(LinearSampler, a, dx, dy).rgb);
            float3 nb = DecodeN(NormalTexture.SampleGrad(LinearSampler, aM, dxM, dyM).rgb);
            nm += wi * (ns + float3(nb.xy * 0.35, 0.0));
        }
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
    if (farW > 0.001 && HasAlbedoTexture != 0)
    {
        float aw, ah, amips;
        AlbedoTexture.GetDimensions(0, aw, ah, amips);
        float3 mean = float3(0.0, 0.0, 0.0);
        [unroll]
        for (int k = 0; k < 4; ++k)
            if (w[k] > 0.002)
                mean += w[k] * SRGBToLinear(AlbedoTexture.SampleLevel(LinearSampler, float2((float)(k & 1) + 0.5, (float)(k >> 1) + 0.5) * 0.5, max(amips - 2.0, 0.0)).rgb);
        albedo = lerp(albedo, mean, farW);
        nm *= 1.0 - farW;
    }
    albedo *= input.tint.rgb;
    albedo *= MacroVariation(input.worldPos.xz);
    float roughness = max(HasRoughnessTexture != 0 ? rough : Roughness, 0.04);
    float metallic = 0.0;
    float ao = AO;

    // normal mapping in the world-aligned frame of the tiled UVs: T follows +X (+u), B follows +Z (+v)
    float3 N = Ng;
    if (HasNormalTexture == 2) nm.z = sqrt(saturate(1.0 - dot(nm.xy, nm.xy)));   // a two-channel (BC5) atlas: z from x / y
    if (HasNormalTexture != 0 && dot(nm, nm) > 1e-6)
    {
        if (UseDirectXNormals == 0) nm.y = -nm.y;
        nm.xy += SandRipples(p, camDist, w.x + 0.5 * w.y);
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
    if (EnvSky.w > 1.5)
    {
        envColor = SampleEnv(R, roughness) * AmbientStrength;   // the real sky, blurred by roughness
    }
    else if (EnvSky.w > 0.5)
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
        float4 ss = SsaoTex.Sample(ScreenSampler, aoUV);
        ambient *= ss.a;
        // screen-space GI (#257 step 1): one bounce of the lit world (sqrt-encoded radiance, x4 range) onto this surface
        if (GiStrength > 0.0) ambient += ss.rgb * ss.rgb * 4.0 * albedo * (1.0 - metallic) * ss.a;
    }

    float3 color = ambient + Lo;
    color = ApplyFog(color, input.worldPos);

    color = TonemapScene(color * (SceneExposure > 0.0 ? SceneExposure : 0.5), Tonemapper);   // the scene exposure, then the curve
    color = pow(max(color, 0.0), 1.0 / 2.2);
    return float4(color, 1.0);
}
