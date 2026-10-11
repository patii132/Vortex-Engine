// Standard PBR shader — the engine's main lit pipeline (drives 3 PSOs: solid, wireframe, double-sided).
// Cook-Torrance GGX + directional/point/spot lights + hemisphere ambient + environment/rim + ACES tonemap.
// World comes per-INSTANCE from the vertex stream (GPU instancing); PerObject.World is unused by the VS.
//
// IMPORTANT: the cbuffer field order + padding and the texture/sampler register bindings are ABI-coupled to the
// C++ PerFrame / PerObject / PointLight / SpotLight structs (and ResourceRegistry texture binding) — do NOT
// reorder or repad. Conventions: row-major matrices, mul(vec, mat).

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
    // Fog (Welle A #27) — byte-matches the appended C++ PerFrameConstants fields (FogColor @128).
    float3 FogColor;
    float FogDensity;
    float FogHeightY;
    float FogHeightFalloff;
    uint FogMode;
    float SceneExposure;   // pre-tonemap multiplier (0.5 = the classic look; <= 0 reads as 0.5)
    // Spot-light shadows (#23) — APPENDED after fog (@160), byte-matched to PerFrameConstants.
    // Per-spot shadow data lives in SpotLight (strength/bias/slot) + the ShadowVP[4] tail of the
    // light buffer; b0 only carries the atlas tile texel size (all tiles are the same size).
    float ShadowMapTexel;
    uint Tonemapper;       // 0 = ACES fit, 1 = PBR Neutral
    uint2 ShadowPadding;
    // SSAO (#32) — APPENDED @176, byte-matched to PerFrameConstants. Screen UV is derived from
    // SV_POSITION and the AO texture's own dimensions, so no screen size travels here.
    float SsaoEnabled;
    float2 SsaoPadding;
    float FogSunGlow;      // aerial perspective: the sun's glow in the fog (0 = off)
    // Sky gradient for specular reflections — APPENDED @192, byte-matched to PerFrameConstants.
    float4 EnvSky;       // rgb zenith, w = 1 when a gradient sky is active
    float4 EnvHorizon;
    float4 EnvGround;
};

// Exp2 distance fog with optional HEIGHT fog (#328): density is uniform up to FogHeightY and falls off as
// exp(-FogHeightFalloff * (y - FogHeightY)) above it, integrated along the camera ray (the camera's own height
// counts, nothing is clamped to a band). Applied in LINEAR space before tonemapping; FogDensity 0 = off.
float FogOpticalDepth(float density, float heightY, float k, float3 cam, float3 worldPos)
{
    float3 delta = worldPos - cam;
    float dist = length(delta);
    if (k <= 0.0) { float d = density * dist; return -log2(max(exp2(-d * d), 1e-6)); }   // uniform fog, the old exp2 look
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
    float2 UVTiling;         // texture repeat scale (mirrors PerObjectConstants at byte offset 128)
    uint HasHeightTexture;   // @136 — parallax/displacement height map bound
    float HeightScale;       // @140 — parallax depth
    float AlphaCutoff;       // @144 — AlphaTest cutoff (#329), 0 = off
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
    // Spot shadows (#23) — packed into the former padding, 64-byte ABI unchanged.
    // shadowSlot: -1 = no shadow; 0..3 = tile index into the shadow atlas at t7.
    float shadowStrength;
    float shadowBias;
    float shadowSlot;
};

cbuffer LightBuffer : register(b2)
{
    PointLight PointLights[MAX_POINT_LIGHTS];
    SpotLight SpotLights[MAX_SPOT_LIGHTS];
    // Shadow atlas view-projections (#23): one per tile, byte-matched to the C++ light-buffer
    // tail @1024 (the buffer was always 1280 bytes — the tail fits exactly).
    row_major float4x4 ShadowVP[4];
    // Cascaded shadow maps for the directional light (#24) — appended tail @1280 (buffer grown
    // 1280 -> 1536). One ortho crop VP per cascade into the 2x2 CSM atlas at t8 (tile c = slot c).
    row_major float4x4 CascadeVP[3];
    float4 CascadeSplits;    // x/y/z = far view-distance of cascade 0/1/2, w = max shadow distance
    float4 DirShadowParams;  // x strength (0..1), y depth bias (NDC), z cascade count (0 = off), w unused
    // Point light cube shadows (#25) — appended @1504 (buffer grown to 2304). Up to 2 shadowed
    // point lights x 6 perspective faces in the t9 atlas (4x3 grid of 1024² tiles).
    float4 PointShadows[2];              // x = point-light index (-1 = unused), y strength, z bias, w unused
    row_major float4x4 PointFaceVP[12];  // shadow slot p, face f (+X,-X,+Y,-Y,+Z,-Z) -> [p*6+f]
    // Sky light (IBL step 1) — appended @2304 (buffer grown to 2448 / 2560 aligned): SH9 irradiance per channel,
    // rgb = coefficient k (basis order Y00, Y1-1, Y10, Y11, Y2-2, Y2-1, Y20, Y21, Y22), w of [0] = 1 when set.
    float4 SkySh[9];
};

Texture2D AlbedoTexture    : register(t0);
Texture2D NormalTexture    : register(t1);
Texture2D MetallicTexture  : register(t2);
Texture2D RoughnessTexture : register(t3);
Texture2D AOTexture        : register(t4);
// t5 is the vertex-stage bone-palette root SRV (skinning). Height/displacement map lives at t6 (pixel).
Texture2D HeightTexture    : register(t6);
// t7: spot-light shadow map (R32_FLOAT depth from the light's view), s1: comparison sampler
// (LESS_EQUAL, border=white so samples outside the map read "lit" — the cone gate masks the rest).
Texture2D ShadowMap        : register(t7);
// t8: the directional light's cascade atlas (#24) — 2x2 tiles, cascade c in tile c.
Texture2D CsmShadowMap     : register(t8);
// t9: the point-light face atlas (#25) — 4x3 grid of 1024² tiles, tile = slot*6 + face.
Texture2D PointShadowMap   : register(t9);
// t10: the blurred half-res SSAO texture (#32) — multiplied into the ambient term only.
Texture2D SsaoTex          : register(t10);
Texture2D EnvMap           : register(t11);   // the sky as a reflection (IBL step 2)
SamplerState LinearSampler : register(s0);
SamplerComparisonState ShadowSampler : register(s1);
SamplerState ScreenSampler : register(s2);   // linear/clamp for screen-space textures (SSAO)

// 1 = fully lit, 0 = fully shadowed (scaled by strength). Hard shadows via a single hardware-PCF
// comparison tap (SampleCmpLevelZero) — a soft PCF kernel is a later upgrade (ShadowMapTexel is
// already plumbed for it). `slot` picks the 2x2 atlas tile this spot rendered its depth into.
float SampleSpotShadow(float3 worldPos, int slot, float strength, float bias)
{
    float4 sp = mul(float4(worldPos, 1.0), ShadowVP[slot]);
    if (sp.w <= 0.0) return 1.0;                       // behind the light -> outside the cone anyway
    float3 ndc = sp.xyz / sp.w;
    float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;   // outside the tile -> lit
    // Map into the tile: clamp half a texel inside so the comparison tap can't bleed into a
    // neighboring tile (the spot-cone gate masks the fringe anyway).
    suv = clamp(suv, ShadowMapTexel * 0.5, 1.0 - ShadowMapTexel * 0.5) * 0.5;
    suv += float2((slot & 1) != 0 ? 0.5 : 0.0, (slot & 2) != 0 ? 0.5 : 0.0);
    float lit = ShadowMap.SampleCmpLevelZero(ShadowSampler, suv, ndc.z - bias);
    return lerp(1.0, lit, saturate(strength));
}

// Directional cascade shadow (#24): try cascades near -> far and take the FIRST whose ortho crop
// contains the point (tight snapped crops make projection-inside more robust than distance-select at
// the boundaries). 3x3 hardware-PCF kernel; per-cascade bias grows with the coarser world-per-texel.
// The sun's shadow filter (Light ▸ Soft): softness <= 1 = the classic 3x3 box; above it a 16-tap Poisson disk of that
// radius in shadow texels — palm shadows on sand get a real penumbra instead of a stair-stepped edge.
static const float2 PoissonDisk16[16] = { float2(-0.94201624,-0.39906216), float2(0.94558609,-0.76890725), float2(-0.094184101,-0.92938870), float2(0.34495938,0.29387760), float2(-0.91588581,0.45771432), float2(-0.81544232,-0.87912464), float2(-0.38277543,0.27676845), float2(0.97484398,0.75648379), float2(0.44323325,-0.97511554), float2(0.53742981,-0.47373420), float2(-0.26496911,-0.41893023), float2(0.79197514,0.19090188), float2(-0.24188840,0.99706507), float2(-0.81409955,0.91437590), float2(0.19984126,0.78641367), float2(0.14383161,-0.14100790) };
float CsmPcf(float2 suv, float depth, float atlasTexel, float softness)
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
    float r = softness * atlasTexel;
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
        // 2% inner border: points near the crop edge fall through to the next (larger) cascade so
        // the PCF kernel never reads a neighboring tile and crop seams stay invisible.
        if (min(suv.x, suv.y) < 0.02 || max(suv.x, suv.y) > 0.98 || ndc.z > 1.0 || ndc.z < 0.0)
            continue;

        // Map into tile c of the 2x2 atlas; keep the whole 3x3 kernel inside the tile.
        float guard = ShadowMapTexel * (max(DirShadowParams.w, 1.0) + 0.5);   // the whole filter stays inside the tile
        suv = clamp(suv, guard, 1.0 - guard) * 0.5;
        suv += float2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);

        float bias = DirShadowParams.y * (1.0 + (float)c) * (0.75 + 0.25 * max(DirShadowParams.w, 1.0));   // a wider filter needs more bias on slopes   // coarser cascade -> more world per texel
        float atlasTexel = ShadowMapTexel * 0.5;             // tile texel -> atlas UV
        return lerp(1.0, CsmPcf(suv, ndc.z - bias, atlasTexel, DirShadowParams.w), saturate(DirShadowParams.x));
    }
    return 1.0;   // beyond every cascade -> lit
}

// Point light cube shadow (#25): pick the face by the major axis of light->pixel, project with
// that face's 90° perspective VP and compare in its atlas tile. The whole light->pixel segment
// lies inside the chosen face's frustum, so every occluder on the ray is in that face's depth map.
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

        // Tile mapping: 4-column grid of 1024² tiles in the 4096x3072 atlas; keep the 3x3 kernel
        // inside the tile (face seams are covered by the adjacent face's frustum overlap).
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
                lit += PointShadowMap.SampleCmpLevelZero(ShadowSampler,
                    auv + float2((float)x, (float)y) * atlasTexel, ndc.z - PointShadows[p].z);
        return lerp(1.0, lit / 9.0, saturate(PointShadows[p].y));
    }
    return 1.0;   // this light casts no shadow
}

struct VS_IN
{
    float3 pos  : POSITION;
    float3 norm : NORMAL;
    float2 uv   : TEXCOORD0;
    // Per-instance world matrix (4 rows) streamed from vertex slot 1 — enables GPU instancing.
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
    float4 tint      : COLOR0;       // per-instance tint (#331)
};

PS_IN VSMain(VS_IN input)
{
    PS_IN output;
    // World comes per-instance from the vertex stream (row-major), not the constant buffer.
    // per-instance tint (#331): the fourth column of the instance matrix carries (r-1, g-1, b-1, a) — an affine
    // matrix never uses it, so an untinted instance is an exact matrix
    float4 tint = float4(1.0 + input.iw0.w, 1.0 + input.iw1.w, 1.0 + input.iw2.w, input.iw3.w);
    float4x4 World = float4x4(float4(input.iw0.xyz, 0), float4(input.iw1.xyz, 0), float4(input.iw2.xyz, 0), float4(input.iw3.xyz, 1));
    float4 worldPos = mul(float4(input.pos, 1), World);
    output.worldPos = worldPos.xyz;
    output.pos = mul(worldPos, ViewProjection);
    output.norm = normalize(mul(input.norm, (float3x3)World));
    output.uv = input.uv;

    // Derive a tangent basis from the normal. Guard on the PRE-normalized cross length: for a flat up-facing
    // surface (N=(0,1,0), a ground/ceiling plane) cross(N,(0,1,0))=(0,0,0) and normalize() would yield NaN — and
    // `length(NaN) < 0.001` is FALSE (all NaN comparisons are false), so the old fallback was dead code and the
    // tangent stayed NaN. That NaN now feeds the parallax path -> NaN UVs -> the whole surface renders black.
    float3 N = output.norm;
    float3 c = cross(N, float3(0, 1, 0));
    float3 T = (dot(c, c) < 1e-6) ? normalize(cross(N, float3(1, 0, 0))) : normalize(c);
    float3 B = normalize(cross(N, T));
    output.tangent = T;
    output.bitangent = B;
    output.tint = tint;

    return output;
}

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

// Packed PBR maps: Has*Texture is 1 + the channel to read (1 R, 2 G, 3 B, 4 A) — glTF / ORM maps keep roughness
// in G, metallic in B and occlusion in R.
float PickChannel(float4 v, uint flag)
{
    return flag == 2 ? v.g : (flag == 3 ? v.b : (flag == 4 ? v.a : v.r));
}

// Per-pixel tangent frame from screen-space derivatives (Schueler, "Normal Mapping Without Precomputed Tangents"):
// meshes carry no tangents, and a frame derived from the normal alone ignores the UV layout, so normal/parallax
// maps on arbitrary UV islands were lit from the wrong side. T follows +u, B follows +v (image-down: the importer
// flips V), so DirectX-convention normal maps apply as-is and OpenGL ones flip green. The determinant's sign keeps
// it independent of the screen's y axis and of mirrored UVs. Leaves T/B untouched where the UVs have no gradient.
void CotangentFrame(float3 N, float3 p, float2 uv, inout float3 T, inout float3 B)
{
    float3 dp1 = ddx(p);
    float3 dp2 = ddy(p);
    float2 duv1 = ddx(uv);
    float2 duv2 = ddy(uv);
    float3 dp2perp = cross(dp2, N);
    float3 dp1perp = cross(N, dp1);
    float3 t = dp2perp * duv1.x + dp1perp * duv2.x;
    float3 b = dp2perp * duv1.y + dp1perp * duv2.y;
    float det = dot(dp1, dp2perp);
    float m = max(dot(t, t), dot(b, b));
    if (m < 1e-30 || abs(det) < 1e-30) return;
    float k = rsqrt(m) * (det < 0.0 ? -1.0 : 1.0);
    T = t * k;
    B = b * k;
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

float4 PSMain(PS_IN input) : SV_TARGET
{
    // Texture repeat scale: multiply UVs so a small tiling texture repeats across a large surface instead of being
    // stretched once (the "blurry ground" bug). Guard against an unset/zero tiling (e.g. a non-material draw) -> 1x.
    float2 tiling = (UVTiling.x > 0.0 && UVTiling.y > 0.0) ? UVTiling : float2(1.0, 1.0);
    float2 uv = input.uv * tiling;

    // Tangent frame for parallax + normal mapping (computed outside any branch: it needs derivatives).
    float3 Ng = normalize(input.norm);
    float3 T = normalize(input.tangent);
    float3 B = normalize(input.bitangent);
    CotangentFrame(Ng, input.worldPos, uv, T, B);

    // Parallax mapping: shift the UVs along the tangent-space view direction by the height map, so a "texture with
    // depth" reads as real relief (stones stand out) instead of a flat decal. Guarded: no height map / zero scale
    // leaves UVs untouched. max(Vt.z,..) tames swimming at grazing angles.
    if (HasHeightTexture != 0 && HeightScale > 0.0) {
        float3 Vw = normalize(CameraPosition - input.worldPos);
        float3x3 TBN = float3x3(normalize(T), normalize(B), Ng);
        float3 Vt = mul(TBN, Vw);
        float h = HeightTexture.Sample(LinearSampler, uv).r;
        uv -= (Vt.xy / max(Vt.z, 0.15)) * ((1.0 - h) * HeightScale);
    }

    float3 albedo = BaseColor.rgb * input.tint.rgb;   // the instance tint multiplies the base colour (#331)
    float alpha = BaseColor.a * input.tint.a;

    if (HasAlbedoTexture != 0) {
        float4 tex = AlbedoTexture.Sample(LinearSampler, uv);
        albedo *= SRGBToLinear(tex.rgb);   // base colour TINTS the texture, alpha multiplies — standard PBR (#330)
        alpha *= tex.a;
    }
    // AlphaTest (#329): cut-outs (foliage, fences, hair cards) drop their transparent texels here
    if (AlphaCutoff > 0.0 && alpha < AlphaCutoff) clip(-1);

    // UNLIT/EMISSIVE PATH - bypass all lighting calculations (for skybox, etc.)
    if (IsUnlit != 0) {
        float3 emissive = albedo * EmissiveStrength;
        emissive = ApplyFog(emissive, input.worldPos);  // fog swallows unlit props too (atmosphere consistency)
        // Apply simple tone mapping for HDR
        emissive = emissive / (emissive + 1.0);
        // Gamma correction
        emissive = pow(emissive, 1.0 / 2.2);
        return float4(emissive, alpha);
    }

    float metallic = Metallic;
    if (HasMetallicTexture != 0) {
        metallic = PickChannel(MetallicTexture.Sample(LinearSampler, uv), HasMetallicTexture);
    }

    float roughness = max(Roughness, 0.04);
    if (HasRoughnessTexture != 0) {
        roughness = max(PickChannel(RoughnessTexture.Sample(LinearSampler, uv), HasRoughnessTexture), 0.04);
    }

    float ao = AO;
    if (HasAOTexture != 0) {
        ao = PickChannel(AOTexture.Sample(LinearSampler, uv), HasAOTexture);
    }

    float3 N = Ng;
    if (HasNormalTexture != 0) {
        float3 normalMap = NormalTexture.Sample(LinearSampler, uv).rgb;
        normalMap = normalMap * 2.0 - 1.0;
        if (UseDirectXNormals == 0) normalMap.y = -normalMap.y;
        normalMap.xy *= NormalStrength;
        float3x3 TBN = float3x3(T, B, N);
        N = normalize(mul(normalMap, TBN));
    }

    float3 V = normalize(CameraPosition - input.worldPos);
    float NdotV = max(dot(N, V), 0.001);

    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

    float3 Lo = float3(0, 0, 0);

    // DIRECTIONAL LIGHT
    if (DirectionalIntensity > 0.001) {
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

        // Cascaded shadow maps (#24): the sun finally throws real shadows.
        radiance *= SampleCascadeShadow(input.worldPos);

        Lo += (kD * albedo / PI + spec) * radiance * NdotL;
    }

    // POINT LIGHTS
    for (uint i = 0; i < PointLightCount && i < MAX_POINT_LIGHTS; ++i) {
        float3 lightVec = PointLights[i].position - input.worldPos;
        float dist = length(lightVec);

        if (dist < PointLights[i].range) {
            float3 L = lightVec / dist;
            float3 H = normalize(V + L);
            float NdotL = max(dot(N, L), 0.0);
            float NdotH = max(dot(N, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);

            float atten = Attenuation(dist, PointLights[i].range);
            float3 radiance = PointLights[i].color * PointLights[i].intensity * atten;

            // Point cube shadows (#25): a no-op for lights without a shadow slot.
            radiance *= SamplePointShadow(input.worldPos, PointLights[i].position, (int)i);

            float D = D_GGX(NdotH, roughness);
            float G = G_Smith(NdotV, NdotL, roughness);
            float3 F = F_Schlick(VdotH, F0);

            float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
            float3 kD = (1.0 - F) * (1.0 - metallic);

            Lo += (kD * albedo / PI + spec) * radiance * NdotL;
        }
    }

    // SPOT LIGHTS
    for (uint j = 0; j < SpotLightCount && j < MAX_SPOT_LIGHTS; ++j) {
        float3 lightVec = SpotLights[j].position - input.worldPos;
        float dist = length(lightVec);

        if (dist < SpotLights[j].range) {
            float3 L = lightVec / dist;
            float3 spotDir = normalize(SpotLights[j].direction);

            float theta = dot(-L, spotDir);
            float outerCos = cos(radians(SpotLights[j].spotAngle * 0.5));
            float innerCos = cos(radians(SpotLights[j].innerSpotAngle * 0.5));
            float spotFade = saturate((theta - outerCos) / (innerCos - outerCos + 0.001));

            if (theta > outerCos) {
                float3 H = normalize(V + L);
                float NdotL = max(dot(N, L), 0.0);
                float NdotH = max(dot(N, H), 0.0);
                float VdotH = max(dot(V, H), 0.0);

                float atten = Attenuation(dist, SpotLights[j].range) * spotFade;
                float3 radiance = SpotLights[j].color * SpotLights[j].intensity * atten;

                // Spot shadow (#23): every spot with an atlas tile samples its own shadow map —
                // the flashlight AND authored scene spots shadow simultaneously (up to 4).
                if (SpotLights[j].shadowSlot >= 0.0)
                    radiance *= SampleSpotShadow(input.worldPos, (int)SpotLights[j].shadowSlot,
                                                 SpotLights[j].shadowStrength, SpotLights[j].shadowBias);

                float D = D_GGX(NdotH, roughness);
                float G = G_Smith(NdotV, NdotL, roughness);
                float3 F = F_Schlick(VdotH, F0);

                float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
                float3 kD = (1.0 - F) * (1.0 - metallic);

                Lo += (kD * albedo / PI + spec) * radiance * NdotL;
            }
        }
    }

    // AMBIENT - Reduced hemisphere lighting for realistic PBR
    float3 skyColor = float3(0.5, 0.55, 0.7);
    float3 groundColor = float3(0.15, 0.15, 0.18);
    float skyAmount = dot(N, float3(0, 1, 0)) * 0.5 + 0.5;
    float3 hemisphereLight = lerp(groundColor, skyColor, skyAmount);
    if (SkySh[0].w > 0.5) hemisphereLight = SkyIrradiance(N);   // the real sky: blue from above, the ground's bounce from below

    float3 ambient = hemisphereLight * AmbientStrength * albedo * ao * (1.0 - metallic);

    // Subtle rim for metals only
    float rimFresnel = pow(saturate(1.0 - NdotV), 5.0);
    float3 rimLight = rimFresnel * F0 * 0.1 * ao * metallic;

    // Environment reflection: the scene's own sky gradient (blurred toward its average with roughness) so metals
    // pick up the sky instead of turning black; neutral dark gradient when no gradient sky is set.
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
    float3 specularAmbient = envColor * envFresnel * ao;

    ambient += specularAmbient + rimLight;

    // SSAO (#32): darken ONLY the ambient/indirect sum — direct light, fog and emissive stay
    // untouched. Screen UV from the pixel position and the half-res AO texture's own dimensions.
    if (SsaoEnabled > 0.5)
    {
        float aoW, aoH;
        SsaoTex.GetDimensions(aoW, aoH);
        float2 aoUV = input.pos.xy / float2(aoW * 2.0, aoH * 2.0);
        ambient *= SsaoTex.Sample(ScreenSampler, aoUV).r;
    }

    float3 color = ambient + Lo;

    // Fog before tonemap (linear space): the flashlight cone "cuts" into the mist because lit
    // fragments still carry their radiance; distant/unlit ones converge to FogColor.
    color = ApplyFog(color, input.worldPos);

    // ACES Filmic Tone Mapping (RRT+ODT fit)
    color = TonemapScene(color * (SceneExposure > 0.0 ? SceneExposure : 0.5), Tonemapper);   // the scene exposure, then the curve

    // Gamma Correction (sRGB)
    color = pow(max(color, 0.0), 1.0 / 2.2);

    return float4(color, alpha);
}

// ---- cut-out shadow casters (#329): the shadow PSO with this pixel shader clips by the albedo alpha, so foliage,
// fences and hair cards cast the shape of their texture instead of a solid quad. Fed by VSMain (PS_IN.uv).
void ShadowCutPS(PS_IN input)
{
    float2 tiling = (UVTiling.x > 0.0 && UVTiling.y > 0.0) ? UVTiling : float2(1.0, 1.0);
    float a = BaseColor.a;
    if (HasAlbedoTexture != 0) a *= AlbedoTexture.Sample(LinearSampler, input.uv * tiling).a;
    if (AlphaCutoff > 0.0 && a < AlphaCutoff) clip(-1);
}
