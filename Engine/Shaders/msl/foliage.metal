// Foliage shader (#125) — the standard PBR shader with WIND: the vertex stage bends every vertex sideways by its height
// above the instance's origin (roots stay, crowns sway), driven per material through fields the foliage never used for
// their original purpose: PerObject.height_scale = floor(sway m × 1000) × 1000 + time s (mod 1000), emissive_strength =
// floor(speed × 10) × 100 + the reference height (m); uv_tiling stays (1, 1) so the cut-out shadow pass keeps sampling
// the right texels. The fragment stage is standard.metal's PSMain with the tiling fixed at 1 and parallax off. Generated from standard.metal —
// keep the two in step when the lighting changes. Bound through the custom material-shader route by the FoliageService.

#include <metal_stdlib>
using namespace metal;

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

struct PerFrame
{
    float4x4 view_projection;          // @0    (row-major in memory -> M * v here == mul(v, M) in HLSL)
    packed_float3 camera_position;     // @64
    float padding0;
    packed_float3 light_direction;     // @80
    float directional_intensity;
    packed_float3 light_color;         // @96
    float ambient_strength;
    uint point_light_count;            // @112
    uint spot_light_count;
    uint frame_padding[2];
    packed_float3 fog_color;           // @128
    float fog_density;
    float fog_height_y;                // @144
    float fog_height_falloff;
    uint fog_mode;
    float scene_exposure;              // @156  pre-tonemap multiplier (0.5 = classic; <= 0 reads as 0.5)
    float shadow_map_texel;            // @160
    uint tonemapper;                   // @164  0 = ACES fit, 1 = PBR Neutral
    float gi_strength;                 // @168  screen-space GI: > 0 = ssao_tex.rgb carries bounce light
    uint shadow_padding1;
    float ssao_enabled;                // @176
    float ssao_padding[2];             // the AO UV scale
    float fog_sun_glow;                // @188  the sun's glow in the fog (0 = off)
    float4 env_sky;                    // @192  scene sky gradient for reflections (rgb); w = 1 when a sky is set
    float4 env_horizon;                // @208
    float4 env_ground;                 // @224
};

struct PerObject
{
    float4x4 world;                    // @0 (unused by the VS: world comes per instance)
    float4 base_color;                 // @64
    float metallic;                    // @80
    float roughness;
    float ao;
    float normal_strength;
    uint has_albedo_texture;           // @96
    uint has_normal_texture;
    uint has_metallic_texture;
    uint has_roughness_texture;
    uint has_ao_texture;               // @112
    uint use_directx_normals;
    uint is_unlit;
    float emissive_strength;
    float2 uv_tiling;                  // @128
    uint has_height_texture;           // @136
    float height_scale;                // @140
    float alpha_cutoff;                // @144 (#329: > 0 = AlphaTest cutoff)
    float _pad0, _pad1, _pad2;
};

struct PointLight
{
    packed_float3 position; float range;
    packed_float3 color;    float intensity;
};

struct SpotLight
{
    packed_float3 position;  float range;
    packed_float3 direction; float spot_angle;
    packed_float3 color;     float intensity;
    float inner_spot_angle;
    float shadow_strength;
    float shadow_bias;
    float shadow_slot;
};

struct LightBuffer
{
    PointLight point_lights[MAX_POINT_LIGHTS];   // @0
    SpotLight  spot_lights[MAX_SPOT_LIGHTS];     // @512
    float4x4   shadow_vp[4];                     // @1024
    float4x4   cascade_vp[3];                    // @1280
    float4     cascade_splits;                   // @1472
    float4     dir_shadow_params;                // @1488
    float4     point_shadows[2];                 // @1504
    float4x4   point_face_vp[12];                // @1536 .. 2304
    float4     sky_sh[9];                        // @2304 .. 2448  sky light: SH9 irradiance per channel, w of [0] = on
};

struct SkinParams
{
    uint bone_base;                    // first matrix of this draw's palette in the bone storage buffer
    uint padding[3];
};

struct VertexIn
{
    float3 pos  [[attribute(0)]];
    float3 norm [[attribute(1)]];
    float2 uv   [[attribute(2)]];
    float4 iw0  [[attribute(3)]];      // per-instance world matrix rows (vertex buffer slot 1, instance rate)
    float4 iw1  [[attribute(4)]];
    float4 iw2  [[attribute(5)]];
    float4 iw3  [[attribute(6)]];
};

struct SkinnedVertexIn
{
    float3 pos          [[attribute(0)]];
    float3 norm         [[attribute(1)]];
    float2 uv           [[attribute(2)]];
    uchar4 bone_indices [[attribute(3)]];
    float4 bone_weights [[attribute(4)]];
    float4 iw0          [[attribute(5)]];
    float4 iw1          [[attribute(6)]];
    float4 iw2          [[attribute(7)]];
    float4 iw3          [[attribute(8)]];
};

struct VSOut
{
    float4 pos [[position]];
    float3 world_pos;
    float3 norm;
    float2 uv;
    float3 tangent;
    float3 bitangent;
    float4 tint;                       // per-instance tint (#331)
};

static inline void tangent_basis(float3 N, thread float3& T, thread float3& B)
{
    float3 c = cross(N, float3(0.0, 1.0, 0.0));
    T = (dot(c, c) < 1e-6) ? normalize(cross(N, float3(1.0, 0.0, 0.0))) : normalize(c);
    B = normalize(cross(N, T));
}

vertex VSOut VSMain(VertexIn in [[stage_in]], constant PerFrame& frame [[buffer(0)]], constant PerObject& obj [[buffer(1)]])
{
    VSOut o;
    float4 tint = float4(1.0 + in.iw0.w, 1.0 + in.iw1.w, 1.0 + in.iw2.w, in.iw3.w);
    float4x4 world = float4x4(float4(in.iw0.xyz, 0.0), float4(in.iw1.xyz, 0.0), float4(in.iw2.xyz, 0.0), float4(in.iw3.xyz, 1.0));
    float4 world_pos = world * float4(in.pos, 1.0);
    // wind (#125): see foliage.hlsl — bend by height above the instance origin, per-instance phase from the world position
    {
        float sway_i = floor(obj.height_scale / 1000.0);
        float t = obj.height_scale - sway_i * 1000.0;
        float sway = sway_i / 1000.0;
        float speed_i = floor(obj.emissive_strength / 100.0);
        float ref_h = max(obj.emissive_strength - speed_i * 100.0, 0.05);
        float speed = max(speed_i / 10.0, 0.01);
        if (sway > 0.0)
        {
            float h = saturate(in.pos.y / ref_h);
            float bend = h * h * sway;
            float phase = dot(in.iw3.xz, float2(0.37, 0.91));
            float w1 = sin(t * 1.3 * speed + phase) * 0.6 + sin(t * 2.9 * speed + phase * 1.7 + in.pos.y * 0.5) * 0.4;
            float flutter = sin(t * 7.0 * speed + in.pos.x * 4.0 + in.pos.z * 4.0 + phase) * 0.12 * h * sway;
            world_pos.xz += float2(0.92, 0.39) * (w1 * bend + flutter);
            world_pos.y -= abs(w1) * bend * 0.12;
        }
    }
    o.world_pos = world_pos.xyz;
    o.pos = frame.view_projection * world_pos;
    float3x3 world3 = float3x3(in.iw0.xyz, in.iw1.xyz, in.iw2.xyz);
    o.norm = normalize(world3 * in.norm);
    o.uv = in.uv;
    tangent_basis(o.norm, o.tangent, o.bitangent);
    o.tint = tint;
    return o;
}

static inline float4x4 load_bone(const device float4* rows, uint i)
{
    uint b = i * 4;
    return float4x4(rows[b], rows[b + 1], rows[b + 2], rows[b + 3]);
}

vertex VSOut VSSkinned(SkinnedVertexIn in [[stage_in]], constant PerFrame& frame [[buffer(0)]],
                       constant SkinParams& skin_params [[buffer(1)]], const device float4* bone_rows [[buffer(2)]])
{
    VSOut o;
    uint base = skin_params.bone_base;
    float4x4 skin =
        in.bone_weights.x * load_bone(bone_rows, base + (uint)in.bone_indices.x) +
        in.bone_weights.y * load_bone(bone_rows, base + (uint)in.bone_indices.y) +
        in.bone_weights.z * load_bone(bone_rows, base + (uint)in.bone_indices.z) +
        in.bone_weights.w * load_bone(bone_rows, base + (uint)in.bone_indices.w);

    float4 skinned_pos = skin * float4(in.pos, 1.0);
    float3x3 skin3 = float3x3(skin[0].xyz, skin[1].xyz, skin[2].xyz);
    float3 skinned_norm = normalize(skin3 * in.norm);

    // per-instance tint (#331): the fourth column of the instance matrix carries (r-1, g-1, b-1, a) — an affine
    // matrix never uses it, so an untinted instance is an exact matrix
    float4 tint = float4(1.0 + in.iw0.w, 1.0 + in.iw1.w, 1.0 + in.iw2.w, in.iw3.w);
    float4x4 world = float4x4(float4(in.iw0.xyz, 0.0), float4(in.iw1.xyz, 0.0), float4(in.iw2.xyz, 0.0), float4(in.iw3.xyz, 1.0));
    float4 world_pos = world * skinned_pos;
    o.world_pos = world_pos.xyz;
    o.pos = frame.view_projection * world_pos;
    float3x3 world3 = float3x3(in.iw0.xyz, in.iw1.xyz, in.iw2.xyz);
    o.norm = normalize(world3 * skinned_norm);
    o.uv = in.uv;
    tangent_basis(o.norm, o.tangent, o.bitangent);
    o.tint = tint;
    return o;
}

// ---- shadow map passes: depth only, the light's view-projection arrives in PerFrame.view_projection ----
vertex float4 ShadowVS(VertexIn in [[stage_in]], constant PerFrame& frame [[buffer(0)]])
{
    float4x4 world = float4x4(in.iw0, in.iw1, in.iw2, in.iw3);
    return frame.view_projection * (world * float4(in.pos, 1.0));
}

vertex float4 ShadowVSSkinnedLayout(SkinnedVertexIn in [[stage_in]], constant PerFrame& frame [[buffer(0)]])
{
    float4x4 world = float4x4(in.iw0, in.iw1, in.iw2, in.iw3);
    return frame.view_projection * (world * float4(in.pos, 1.0));
}

fragment void ShadowPS() {}

// cut-out casters (#329): the albedo alpha decides what casts — foliage, fences, hair cards
struct ShadowCutOut { float4 pos [[position]]; float2 uv; };
vertex ShadowCutOut ShadowVSCut(VertexIn in [[stage_in]], constant PerFrame& frame [[buffer(0)]])
{
    float4x4 world = float4x4(in.iw0, in.iw1, in.iw2, in.iw3);
    ShadowCutOut o;
    o.pos = frame.view_projection * (world * float4(in.pos, 1.0));
    o.uv = in.uv;
    return o;
}

vertex ShadowCutOut ShadowVSCutSkinnedLayout(SkinnedVertexIn in [[stage_in]], constant PerFrame& frame [[buffer(0)]])
{
    float4x4 world = float4x4(in.iw0, in.iw1, in.iw2, in.iw3);
    ShadowCutOut o;
    o.pos = frame.view_projection * (world * float4(in.pos, 1.0));
    o.uv = in.uv;
    return o;
}

fragment void ShadowPSCut(ShadowCutOut in [[stage_in]], constant PerObject& obj [[buffer(1)]],
                          texture2d<float> albedo_tex [[texture(0)]], sampler albedo_smp [[sampler(0)]])
{
    float2 tiling = (obj.uv_tiling.x > 0.0 && obj.uv_tiling.y > 0.0) ? obj.uv_tiling : float2(1.0, 1.0);
    float a = obj.base_color.a;
    if (obj.has_albedo_texture != 0) a *= albedo_tex.sample(albedo_smp, in.uv * tiling).a;
    if (obj.alpha_cutoff > 0.0 && a < obj.alpha_cutoff) discard_fragment();
}

static inline float fog_optical_depth(float density, float height_y, float k, float3 cam, float3 world_pos)
{
    float3 delta = world_pos - cam;
    float dist = length(delta);
    if (k <= 0.0) { float d = density * dist; return -log2(max(exp2(-d * d), 1e-6)); }   // uniform fog, the old exp2 look
    // Height fog (#328): density is uniform up to height_y and falls off as exp(-k * (y - height_y)) above it; the
    // ray is split at the ceiling and both parts are integrated, so the camera's own height counts and nothing is
    // clamped to a band below height_y.
    float ya = cam.y - height_y, yb = world_pos.y - height_y;
    if (ya <= 0.0 && yb <= 0.0) return density * dist;
    float t0 = 0.0, t1 = 1.0;
    if (ya <= 0.0) t0 = -ya / (yb - ya);
    else if (yb <= 0.0) t1 = ya / (ya - yb);
    float above = (t1 - t0) * dist, below = dist - above;
    float hya = max(ya, 0.0), hyb = max(yb, 0.0), dyv = hyb - hya;
    float mean = abs(dyv) > 1e-3 ? (exp(-k * hya) - exp(-k * hyb)) / (k * dyv) : exp(-k * hya);
    return density * (below + above * mean);
}

// The scene's tonemapper (frame.tonemapper): 0 = the ACES fit (classic, pulls bright saturated colours towards white);
// 1 = Khronos PBR Neutral (hue-preserving with a gentle highlight desaturation). Input = the EXPOSED scene colour.
static inline float3 tonemap_neutral(float3 color)
{
    const float start_compression = 0.8 - 0.04;
    const float desaturation = 0.15;
    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    color -= offset;
    float peak = max(color.r, max(color.g, color.b));
    if (peak < start_compression) return color;
    const float d = 1.0 - start_compression;
    float new_peak = 1.0 - d * d / (peak + d - start_compression);
    color *= new_peak / peak;
    float g = 1.0 - 1.0 / (desaturation * (peak - new_peak) + 1.0);
    return mix(color, float3(new_peak), g);
}
static inline float3 tonemap_scene(float3 x, uint mode)
{
    if (mode == 1u) return saturate(tonemap_neutral(max(x, 0.0)));
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    return saturate(a / b);
}

// Aerial perspective: the flat fog colour plus the sun's forward-scatter glow when looking towards it (fog_sun_glow 0 = off).
static inline float3 fog_inscatter(constant PerFrame& frame, float3 world_pos)
{
    float3 c = float3(frame.fog_color);
    if (frame.fog_sun_glow > 0.0 && frame.directional_intensity > 0.001)
    {
        float3 V = normalize(world_pos - float3(frame.camera_position));
        float mu = saturate(dot(V, normalize(-float3(frame.light_direction))));
        float phase = 0.12 * mu * mu + 0.88 * pow(mu, 10.0);
        c += float3(frame.light_color) * (frame.fog_sun_glow * phase);
    }
    return c;
}

static inline float3 apply_fog(constant PerFrame& frame, float3 color, float3 world_pos)
{
    if (frame.fog_density <= 0.0) return color;
    float optical = fog_optical_depth(frame.fog_density, frame.fog_height_y, frame.fog_height_falloff, float3(frame.camera_position), world_pos);
    float f = 1.0 - exp2(-optical);
    return mix(color, fog_inscatter(frame, world_pos), saturate(f));
}

static inline float d_ggx(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float d = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d + 0.0001);
}

static inline float g_schlick_ggx(float NdotV, float roughness)
{
    float k = (roughness + 1.0);
    k = (k * k) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k + 0.0001);
}

static inline float g_smith(float NdotV, float NdotL, float roughness)
{
    return g_schlick_ggx(NdotV, roughness) * g_schlick_ggx(NdotL, roughness);
}

static inline float3 f_schlick(float VdotH, float3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);
}

static inline float3 srgb_to_linear(float3 color)
{
    return pow(max(color, 0.0), 2.2);
}

static inline float attenuation(float distance, float range)
{
    float d = distance / range;
    float atten = saturate(1.0 - d * d);
    return atten * atten / (distance * distance + 0.01);
}

// ---- shadow sampling (byte-matched to standard.hlsl: spot atlas 2x2 tiles, cascade atlas 2x2 tiles,
//      point atlas 4x3 tiles of 1024²). depth2d + comparison sampler = hardware PCF tap. 1 = lit. ----
static inline float sample_spot_shadow(constant PerFrame& frame, constant LightBuffer& lights,
                                       depth2d<float> atlas, sampler smp, float3 wp, int slot, float strength, float bias)
{
    float4 sp = lights.shadow_vp[slot] * float4(wp, 1.0);
    if (sp.w <= 0.0) return 1.0;
    float3 ndc = sp.xyz / sp.w;
    float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;
    float t = frame.shadow_map_texel;
    suv = clamp(suv, t * 0.5, 1.0 - t * 0.5) * 0.5;
    suv += float2((slot & 1) != 0 ? 0.5 : 0.0, (slot & 2) != 0 ? 0.5 : 0.0);
    float lit = atlas.sample_compare(smp, suv, ndc.z - bias);
    return mix(1.0, lit, saturate(strength));
}

// The sun's shadow filter (Light ▸ Soft): softness <= 1 = the classic 3x3 box; above it a 16-tap Poisson disk of that
// radius in shadow texels — a real penumbra under palms instead of a stair-stepped edge.
constant float2 poisson16[16] = { float2(-0.94201624,-0.39906216), float2(0.94558609,-0.76890725), float2(-0.094184101,-0.92938870), float2(0.34495938,0.29387760), float2(-0.91588581,0.45771432), float2(-0.81544232,-0.87912464), float2(-0.38277543,0.27676845), float2(0.97484398,0.75648379), float2(0.44323325,-0.97511554), float2(0.53742981,-0.47373420), float2(-0.26496911,-0.41893023), float2(0.79197514,0.19090188), float2(-0.24188840,0.99706507), float2(-0.81409955,0.91437590), float2(0.19984126,0.78641367), float2(0.14383161,-0.14100790) };
static inline float csm_pcf(depth2d<float> atlas, sampler smp, float2 suv, float depth, float atlas_texel, float softness)
{
    float lit = 0.0;
    if (softness <= 1.01)
    {
        for (int y = -1; y <= 1; ++y)
            for (int x = -1; x <= 1; ++x)
                lit += atlas.sample_compare(smp, suv + float2((float)x, (float)y) * atlas_texel, depth);
        return lit / 9.0;
    }
    float r = softness * atlas_texel;
    for (int i = 0; i < 16; ++i)
        lit += atlas.sample_compare(smp, suv + poisson16[i] * r, depth);
    return lit / 16.0;
}

static inline float sample_cascade_shadow(constant PerFrame& frame, constant LightBuffer& lights,
                                          depth2d<float> atlas, sampler smp, float3 wp)
{
    int count = (int)lights.dir_shadow_params.z;
    if (count <= 0) return 1.0;
    for (int c = 0; c < 3; ++c)
    {
        if (c >= count) break;
        float4 sp = lights.cascade_vp[c] * float4(wp, 1.0);
        if (sp.w <= 0.0) continue;
        float3 ndc = sp.xyz / sp.w;
        float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (min(suv.x, suv.y) < 0.02 || max(suv.x, suv.y) > 0.98 || ndc.z > 1.0 || ndc.z < 0.0) continue;
        float t = frame.shadow_map_texel;
        float guard = t * (max(lights.dir_shadow_params.w, 1.0) + 0.5);   // the whole filter stays inside the tile
        suv = clamp(suv, guard, 1.0 - guard) * 0.5;
        suv += float2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
        float bias = lights.dir_shadow_params.y * (1.0 + (float)c) * (0.75 + 0.25 * max(lights.dir_shadow_params.w, 1.0));
        float atlas_texel = t * 0.5;
        return mix(1.0, csm_pcf(atlas, smp, suv, ndc.z - bias, atlas_texel, lights.dir_shadow_params.w), saturate(lights.dir_shadow_params.x));
    }
    return 1.0;
}

static inline float sample_point_shadow(constant LightBuffer& lights, depth2d<float> atlas, sampler smp,
                                        float3 wp, float3 lpos, int light_index)
{
    for (int p = 0; p < 2; ++p)
    {
        if ((int)lights.point_shadows[p].x != light_index) continue;
        float3 d = wp - lpos;
        float3 ad = abs(d);
        int face;
        if (ad.x >= ad.y && ad.x >= ad.z) face = d.x > 0.0 ? 0 : 1;
        else if (ad.y >= ad.z)            face = d.y > 0.0 ? 2 : 3;
        else                              face = d.z > 0.0 ? 4 : 5;
        float4 sp = lights.point_face_vp[p * 6 + face] * float4(wp, 1.0);
        if (sp.w <= 0.0) return 1.0;
        float3 ndc = sp.xyz / sp.w;
        float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;
        const float tile_texel = 1.0 / 1024.0;
        suv = clamp(suv, tile_texel * 1.5, 1.0 - tile_texel * 1.5);
        int tile = p * 6 + face;
        float2 auv = (suv + float2((float)(tile & 3), (float)(tile >> 2))) * float2(0.25, 1.0 / 3.0);
        const float2 atlas_texel = float2(1.0 / 4096.0, 1.0 / 3072.0);
        float bias = lights.point_shadows[p].z;
        float lit = 0.0;
        for (int y = -1; y <= 1; ++y)
            for (int x = -1; x <= 1; ++x)
                lit += atlas.sample_compare(smp, auv + float2((float)x, (float)y) * atlas_texel, ndc.z - bias);
        return mix(1.0, lit / 9.0, saturate(lights.point_shadows[p].y));
    }
    return 1.0;
}

// Packed PBR maps: PerObject::has_*_texture is 1 + the channel to read (1 R, 2 G, 3 B, 4 A) — glTF / ORM maps
// keep roughness in G, metallic in B and occlusion in R.
static inline float pick_channel(float4 v, uint flag)
{
    return flag == 2u ? v.g : (flag == 3u ? v.b : (flag == 4u ? v.a : v.r));
}

// Per-pixel tangent frame from screen-space derivatives (Schueler, "Normal Mapping Without Precomputed Tangents"):
// meshes carry no tangents, and a frame derived from the normal alone ignores the UV layout, so normal/parallax
// maps on arbitrary UV islands were lit from the wrong side. T follows +u, B follows +v (image-down: the importer
// flips V), so DirectX-convention normal maps apply as-is and OpenGL ones flip green. The determinant's sign keeps
// it independent of the screen's y axis and of mirrored UVs. Leaves T/B untouched where the UVs have no gradient.
static inline void cotangent_frame(float3 N, float3 p, float2 uv, thread float3& T, thread float3& B)
{
    float3 dp1 = dfdx(p), dp2 = dfdy(p);
    float2 duv1 = dfdx(uv), duv2 = dfdy(uv);
    float3 dp2perp = cross(dp2, N), dp1perp = cross(N, dp1);
    float3 t = dp2perp * duv1.x + dp1perp * duv2.x;
    float3 b = dp2perp * duv1.y + dp1perp * duv2.y;
    float det = dot(dp1, dp2perp);
    float m = max(dot(t, t), dot(b, b));
    if (m < 1e-30 || abs(det) < 1e-30) return;
    float k = rsqrt(m) * (det < 0.0 ? -1.0 : 1.0);
    T = t * k;
    B = b * k;
}

// Sky light (IBL step 1): E(n)/π from the SH9 irradiance baked off the sky picture (cosine-lobe weights 1, 2/3, 1/4)
static inline float3 sky_irradiance(constant LightBuffer& lights, float3 n)
{
    float x = n.x, y = n.y, z = n.z;
    float3 e = lights.sky_sh[0].rgb * 0.282095;
    e += (2.0 / 3.0) * (lights.sky_sh[1].rgb * (0.488603 * y) + lights.sky_sh[2].rgb * (0.488603 * z) + lights.sky_sh[3].rgb * (0.488603 * x));
    e += 0.25 * (lights.sky_sh[4].rgb * (1.092548 * x * y) + lights.sky_sh[5].rgb * (1.092548 * y * z) + lights.sky_sh[6].rgb * (0.315392 * (3.0 * z * z - 1.0))
               + lights.sky_sh[7].rgb * (1.092548 * x * z) + lights.sky_sh[8].rgb * (0.546274 * (x * x - y * y)));
    return max(e, 0.0);
}

// The sky as a reflection (IBL step 2): an equirect of the sky with mips at texture(10), env_sky.w = 2 while bound,
// env_horizon.w = its mip count; the baker's 8-bit soft compression (straight below 1, x / (1 + x) above) undone here.
static inline float2 env_uv(float3 d)
{
    return float2(atan2(d.x, d.z) / (2.0 * 3.14159265) + 0.5, acos(clamp(d.y, -1.0, 1.0)) / 3.14159265);
}
static inline float3 decode_env(float3 c)
{
    float3 lo = c / 0.75;
    float3 t = saturate((c - 0.75) / 0.25);
    float3 hi = 1.0 / max(1.0 - t, 0.0625);
    return (c.x <= 0.75 && c.y <= 0.75 && c.z <= 0.75) ? lo : max(lo, hi);
}
static inline float3 sample_env(texture2d<float> env_tex, sampler env_smp, float3 R, float roughness, float mips)
{
    float lod = saturate(roughness) * max(mips - 1.0, 0.0);
    return decode_env(env_tex.sample(env_smp, env_uv(R), level(lod)).rgb);
}

fragment float4 PSMain(VSOut in [[stage_in]],
                       bool isFront [[front_facing]],
                       constant PerFrame& frame [[buffer(0)]],
                       constant PerObject& obj [[buffer(1)]],
                       constant LightBuffer& lights [[buffer(2)]],
                       texture2d<float> albedo_tex    [[texture(0)]], sampler albedo_smp    [[sampler(0)]],
                       texture2d<float> normal_tex    [[texture(1)]], sampler normal_smp    [[sampler(1)]],
                       texture2d<float> metallic_tex  [[texture(2)]], sampler metallic_smp  [[sampler(2)]],
                       texture2d<float> roughness_tex [[texture(3)]], sampler roughness_smp [[sampler(3)]],
                       texture2d<float> ao_tex        [[texture(4)]], sampler ao_smp        [[sampler(4)]],
                       texture2d<float> height_tex    [[texture(5)]], sampler height_smp    [[sampler(5)]],
                       depth2d<float>   spot_shadow   [[texture(6)]], sampler shadow_smp    [[sampler(6)]],
                       depth2d<float>   csm_shadow    [[texture(7)]], sampler csm_smp       [[sampler(7)]],
                       depth2d<float>   point_shadow  [[texture(8)]], sampler point_smp     [[sampler(8)]],
                       texture2d<float> ssao_tex      [[texture(9)]], sampler ssao_smp      [[sampler(9)]],
                       texture2d<float> env_tex       [[texture(10)]], sampler env_smp       [[sampler(10)]])
{
    float2 uv = in.uv;   // the wind lives in uv_tiling: no texture tiling

    float3 cam_pos = float3(frame.camera_position);

    float3 Ng = normalize(in.norm);
    if (!isFront) Ng = -Ng;   // a leaf seen from behind is lit on that side (double-sided cut-outs)
    float3 T = normalize(in.tangent), B = normalize(in.bitangent);
    cotangent_frame(Ng, in.world_pos, uv, T, B);   // outside any branch: it needs derivatives

    if (false)   // parallax off: height_scale carries the wind time
    {
        float3 Vw = normalize(cam_pos - in.world_pos);
        float3x3 TBN = float3x3(normalize(T), normalize(B), Ng);
        // HLSL mul(TBN, V) with TBN rows = T,B,N  ->  dot each row with V
        float3 Vt = float3(dot(TBN[0], Vw), dot(TBN[1], Vw), dot(TBN[2], Vw));
        float h = height_tex.sample(height_smp, uv).r;
        uv -= (Vt.xy / max(Vt.z, 0.15)) * ((1.0 - h) * obj.height_scale);
    }

    float3 albedo = obj.base_color.rgb * in.tint.rgb;   // the instance tint multiplies the base colour (#331)
    float alpha = obj.base_color.a * in.tint.a;
    if (obj.has_albedo_texture != 0)
    {
        float4 tex = albedo_tex.sample(albedo_smp, uv);
        albedo *= srgb_to_linear(tex.rgb);   // base colour TINTS the texture, alpha multiplies — standard PBR (#330)
        alpha *= tex.a;
    }
    // AlphaTest (#329): cut-outs (foliage, fences, hair cards) drop their transparent texels here
    if (obj.alpha_cutoff > 0.0 && alpha < obj.alpha_cutoff) discard_fragment();

    if (obj.is_unlit != 0)
    {
        float3 emissive = albedo * obj.emissive_strength;
        emissive = apply_fog(frame, emissive, in.world_pos);
        emissive = emissive / (emissive + 1.0);
        emissive = pow(emissive, 1.0 / 2.2);
        return float4(emissive, alpha);
    }

    float metallic = obj.metallic;
    if (obj.has_metallic_texture != 0) metallic = pick_channel(metallic_tex.sample(metallic_smp, uv), obj.has_metallic_texture);

    float roughness = max(obj.roughness, 0.04);
    if (obj.has_roughness_texture != 0) roughness = max(pick_channel(roughness_tex.sample(roughness_smp, uv), obj.has_roughness_texture), 0.04);

    float ao = obj.ao;
    if (obj.has_ao_texture != 0) ao = pick_channel(ao_tex.sample(ao_smp, uv), obj.has_ao_texture);

    float3 N = Ng;
    if (obj.has_normal_texture != 0)
    {
        float3 nm = normal_tex.sample(normal_smp, uv).rgb * 2.0 - 1.0;
        if (obj.has_normal_texture == 2) nm.z = sqrt(saturate(1.0 - dot(nm.xy, nm.xy)));   // a two-channel (BC5) map
        if (obj.use_directx_normals == 0) nm.y = -nm.y;
        nm.xy *= obj.normal_strength;
        N = normalize(nm.x * T + nm.y * B + nm.z * N);
    }

    float3 V = normalize(cam_pos - in.world_pos);
    float NdotV = max(dot(N, V), 0.001);
    float3 F0 = mix(float3(0.04), albedo, metallic);
    float3 Lo = float3(0.0);

    if (frame.directional_intensity > 0.001)
    {
        float3 L = normalize(-float3(frame.light_direction));
        float3 H = normalize(V + L);
        float NdotL = max(dot(N, L), 0.0);
        float NdotH = max(dot(N, H), 0.0);
        float VdotH = max(dot(V, H), 0.0);
        float D = d_ggx(NdotH, roughness);
        float G = g_smith(NdotV, NdotL, roughness);
        float3 F = f_schlick(VdotH, F0);
        float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
        float3 kD = (1.0 - F) * (1.0 - metallic);
        float3 radiance = float3(frame.light_color) * frame.directional_intensity;
        float sun_shadow = sample_cascade_shadow(frame, lights, csm_shadow, csm_smp, in.world_pos);
        // leaves: the diffuse term wraps round the thin blade and the sun shines through it (translucency)
        float NdotLw = saturate((dot(N, L) + 0.4) / 1.4);
        float3 trans = albedo * radiance * pow(saturate(dot(V, -L)), 3.0) * 0.35 * (1.0 - metallic);
        Lo += ((kD * albedo / PI) * radiance * NdotLw + spec * radiance * NdotL + trans / PI) * sun_shadow;
    }

    for (uint i = 0; i < frame.point_light_count && i < MAX_POINT_LIGHTS; ++i)
    {
        float3 lpos = float3(lights.point_lights[i].position);
        float3 light_vec = lpos - in.world_pos;
        float dist = length(light_vec);
        if (dist < lights.point_lights[i].range)
        {
            float3 L = light_vec / dist;
            float3 H = normalize(V + L);
            float NdotL = max(dot(N, L), 0.0);
            float NdotH = max(dot(N, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);
            float atten = attenuation(dist, lights.point_lights[i].range);
            atten *= sample_point_shadow(lights, point_shadow, point_smp, in.world_pos, lpos, (int)i);
            float3 radiance = float3(lights.point_lights[i].color) * lights.point_lights[i].intensity * atten;
            float D = d_ggx(NdotH, roughness);
            float G = g_smith(NdotV, NdotL, roughness);
            float3 F = f_schlick(VdotH, F0);
            float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
            float3 kD = (1.0 - F) * (1.0 - metallic);
            Lo += (kD * albedo / PI + spec) * radiance * NdotL;
        }
    }

    for (uint j = 0; j < frame.spot_light_count && j < MAX_SPOT_LIGHTS; ++j)
    {
        float3 lpos = float3(lights.spot_lights[j].position);
        float3 light_vec = lpos - in.world_pos;
        float dist = length(light_vec);
        if (dist < lights.spot_lights[j].range)
        {
            float3 L = light_vec / dist;
            float3 spot_dir = normalize(float3(lights.spot_lights[j].direction));
            float theta = dot(-L, spot_dir);
            float outer_cos = cos(lights.spot_lights[j].spot_angle * 0.5 * PI / 180.0);
            float inner_cos = cos(lights.spot_lights[j].inner_spot_angle * 0.5 * PI / 180.0);
            float spot_fade = saturate((theta - outer_cos) / (inner_cos - outer_cos + 0.001));
            if (theta > outer_cos)
            {
                float3 H = normalize(V + L);
                float NdotL = max(dot(N, L), 0.0);
                float NdotH = max(dot(N, H), 0.0);
                float VdotH = max(dot(V, H), 0.0);
                float atten = attenuation(dist, lights.spot_lights[j].range) * spot_fade;
                if (lights.spot_lights[j].shadow_slot >= 0.0)
                    atten *= sample_spot_shadow(frame, lights, spot_shadow, shadow_smp, in.world_pos,
                                                (int)lights.spot_lights[j].shadow_slot,
                                                lights.spot_lights[j].shadow_strength, lights.spot_lights[j].shadow_bias);
                float3 radiance = float3(lights.spot_lights[j].color) * lights.spot_lights[j].intensity * atten;
                float D = d_ggx(NdotH, roughness);
                float G = g_smith(NdotV, NdotL, roughness);
                float3 F = f_schlick(VdotH, F0);
                float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
                float3 kD = (1.0 - F) * (1.0 - metallic);
                Lo += (kD * albedo / PI + spec) * radiance * NdotL;
            }
        }
    }

    float3 sky_color = float3(0.5, 0.55, 0.7);
    float3 ground_color = float3(0.15, 0.15, 0.18);
    float sky_amount = dot(N, float3(0.0, 1.0, 0.0)) * 0.5 + 0.5;
    float3 hemisphere = mix(ground_color, sky_color, sky_amount);
    if (lights.sky_sh[0].w > 0.5) hemisphere = sky_irradiance(lights, N);   // the real sky: blue from above, the ground's bounce from below
    float3 ambient = hemisphere * frame.ambient_strength * albedo * ao * (1.0 - metallic);
    if (frame.ssao_enabled > 0.5)
    {
        float2 auv = in.pos.xy * float2(frame.ssao_padding[0], frame.ssao_padding[1]);
        float4 ss = ssao_tex.sample(ssao_smp, auv);
        ambient *= ss.a;
        if (frame.gi_strength > 0.0) ambient += ss.rgb * ss.rgb * 4.0 * albedo * (1.0 - metallic) * ss.a;   // screen-space GI (#257 step 1)
    }

    float rim_fresnel = pow(saturate(1.0 - NdotV), 5.0);
    float3 rim_light = rim_fresnel * F0 * 0.1 * ao * metallic;

    float3 R = reflect(-V, N);
    float3 env_color;
    if (frame.env_sky.w > 1.5)
    {
        env_color = sample_env(env_tex, env_smp, R, roughness, frame.env_horizon.w) * frame.ambient_strength;   // the real sky, blurred by roughness
    }
    else if (frame.env_sky.w > 0.5)
    {
        // Reflections of the scene's own sky gradient (zenith / horizon / ground), blurred toward its average with
        // roughness — metals (weapons!) pick up the sky instead of turning black. Scaled like the diffuse ambient.
        float3 sky_dir = R.y >= 0.0 ? mix(frame.env_horizon.rgb, frame.env_sky.rgb, pow(saturate(R.y), 0.6))
                                    : mix(frame.env_horizon.rgb, frame.env_ground.rgb, pow(saturate(-R.y), 0.6));
        float3 sky_avg = (frame.env_sky.rgb + 2.0 * frame.env_horizon.rgb + frame.env_ground.rgb) * 0.25;
        env_color = mix(sky_dir, sky_avg, saturate(roughness * roughness * 1.5)) * frame.ambient_strength;
    }
    else
    {
        float up_factor = R.y * 0.5 + 0.5;
        env_color = mix(float3(0.01, 0.01, 0.02), float3(0.08, 0.10, 0.15), up_factor);
        env_color = mix(env_color, env_color * 0.2, roughness * roughness);
    }
    float3 env_fresnel = F0 + (max(float3(1.0 - roughness), F0) - F0) * pow(1.0 - NdotV, 5.0);
    float3 specular_ambient = env_color * env_fresnel * ao;
    ambient += specular_ambient + rim_light;

    float3 color = ambient + Lo;
    color = apply_fog(frame, color, in.world_pos);

    // ACES filmic (RRT+ODT fit)
    color = tonemap_scene(color * (frame.scene_exposure > 0.0 ? frame.scene_exposure : 0.5), frame.tonemapper);   // the scene exposure, then the curve
    color = pow(max(color, 0.0), 1.0 / 2.2);
    return float4(color, alpha);
}
