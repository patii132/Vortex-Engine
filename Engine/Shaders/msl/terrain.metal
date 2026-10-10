// Terrain shader (#124) — Metal twin of terrain.hlsl: the heightfield terrain's material shader, bound per terrain
// through the custom material-shader route (standard bindings: PerFrame / PerObject / LightBuffer, the six material
// slots, the three shadow atlases and SSAO). VSMain equals standard.metal's, so the shadow / SSAO passes keep working.
//
// Splat mapping with four PBR layers out of the material slots:
//   texture(0) albedo atlas (2x2: layer i in quadrant (i & 1, i >> 1)), texture(1) normal atlas, texture(2) the splat
//   map (RGBA = the weights of layers 0..3, uv 0..1 across the terrain), texture(3) roughness atlas (red);
//   PerObject.base_color = tiles per metre of layers 0..3 — the layer UVs come from the world XZ position.
#include <metal_stdlib>
using namespace metal;

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

struct PerFrame
{
    float4x4 view_projection;
    packed_float3 camera_position;
    float padding0;
    packed_float3 light_direction;
    float directional_intensity;
    packed_float3 light_color;
    float ambient_strength;
    uint point_light_count;
    uint spot_light_count;
    uint frame_padding[2];
    packed_float3 fog_color;
    float fog_density;
    float fog_height_y;
    float fog_height_falloff;
    uint fog_mode;
    float fog_padding;
    float shadow_map_texel;
    uint shadow_padding[3];
    float ssao_enabled;
    float ssao_padding[3];
    float4 env_sky;
    float4 env_horizon;
    float4 env_ground;
};

struct PerObject
{
    float4x4 world;
    float4 base_color;                 // terrain: tiles per metre of layers 0..3
    float metallic;
    float roughness;                   // terrain: the roughness when no roughness atlas is bound
    float ao;
    float normal_strength;
    uint has_albedo_texture;
    uint has_normal_texture;
    uint has_metallic_texture;         // terrain: the splat map is bound
    uint has_roughness_texture;
    uint has_ao_texture;
    uint use_directx_normals;
    uint is_unlit;
    float emissive_strength;
    float2 uv_tiling;
    uint has_height_texture;
    float height_scale;
    float alpha_cutoff;
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
    PointLight point_lights[MAX_POINT_LIGHTS];
    SpotLight  spot_lights[MAX_SPOT_LIGHTS];
    float4x4   shadow_vp[4];
    float4x4   cascade_vp[3];
    float4     cascade_splits;
    float4     dir_shadow_params;
    float4     point_shadows[2];
    float4x4   point_face_vp[12];
    float4     sky_sh[9];                        // @2304 .. 2448  sky light: SH9 irradiance per channel, w of [0] = on
};

struct VertexIn
{
    float3 pos  [[attribute(0)]];
    float3 norm [[attribute(1)]];
    float2 uv   [[attribute(2)]];
    float4 iw0  [[attribute(3)]];
    float4 iw1  [[attribute(4)]];
    float4 iw2  [[attribute(5)]];
    float4 iw3  [[attribute(6)]];
};

struct VSOut
{
    float4 pos [[position]];
    float3 world_pos;
    float3 norm;
    float2 uv;
    float3 tangent;
    float3 bitangent;
    float4 tint;
};

static inline void tangent_basis(float3 N, thread float3& T, thread float3& B)
{
    float3 c = cross(N, float3(0.0, 1.0, 0.0));
    T = (dot(c, c) < 1e-6) ? normalize(cross(N, float3(1.0, 0.0, 0.0))) : normalize(c);
    B = normalize(cross(N, T));
}

vertex VSOut VSMain(VertexIn in [[stage_in]], constant PerFrame& frame [[buffer(0)]])
{
    VSOut o;
    float4 tint = float4(1.0 + in.iw0.w, 1.0 + in.iw1.w, 1.0 + in.iw2.w, in.iw3.w);
    float4x4 world = float4x4(float4(in.iw0.xyz, 0.0), float4(in.iw1.xyz, 0.0), float4(in.iw2.xyz, 0.0), float4(in.iw3.xyz, 1.0));
    float4 world_pos = world * float4(in.pos, 1.0);
    o.world_pos = world_pos.xyz;
    o.pos = frame.view_projection * world_pos;
    float3x3 world3 = float3x3(in.iw0.xyz, in.iw1.xyz, in.iw2.xyz);
    o.norm = normalize(world3 * in.norm);
    o.uv = in.uv;
    tangent_basis(o.norm, o.tangent, o.bitangent);
    o.tint = tint;
    return o;
}

// ---------------------------------------------------------------- fog / BRDF / shadows (standard.metal)
static inline float fog_optical_depth(float density, float height_y, float k, float3 cam, float3 world_pos)
{
    float3 delta = world_pos - cam;
    float dist = length(delta);
    if (k <= 0.0) { float d = density * dist; return -log2(max(exp2(-d * d), 1e-6)); }
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

static inline float3 apply_fog(constant PerFrame& frame, float3 color, float3 world_pos)
{
    if (frame.fog_density <= 0.0) return color;
    float optical = fog_optical_depth(frame.fog_density, frame.fog_height_y, frame.fog_height_falloff, float3(frame.camera_position), world_pos);
    float f = 1.0 - exp2(-optical);
    return mix(color, float3(frame.fog_color), saturate(f));
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
        suv = clamp(suv, t * 1.5, 1.0 - t * 1.5) * 0.5;
        suv += float2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
        float bias = lights.dir_shadow_params.y * (1.0 + (float)c);
        float atlas_texel = t * 0.5;
        float lit = 0.0;
        for (int y = -1; y <= 1; ++y)
            for (int x = -1; x <= 1; ++x)
                lit += atlas.sample_compare(smp, suv + float2((float)x, (float)y) * atlas_texel, ndc.z - bias);
        return mix(1.0, lit / 9.0, saturate(lights.dir_shadow_params.x));
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

// ---------------------------------------------------------------- splat layers (see terrain.hlsl)
static inline float2 atlas_uv(float2 uv, int layer)
{
    const float pad = 1.0 / 128.0;
    float2 q = float2((float)(layer & 1), (float)(layer >> 1));
    return (q + pad + fract(uv) * (1.0 - 2.0 * pad)) * 0.5;
}

static inline void clamp_grad(thread float2& dx, thread float2& dy)
{
    const float max_g = 1.0 / 16.0;
    float gl = max(length(dx), length(dy));
    if (gl > max_g) { float s = max_g / gl; dx *= s; dy *= s; }
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

// Macro variation: a 2-octave value noise over the world XZ scales the blended albedo by ±9 % (no extra samples)
static inline float macro_hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
static inline float macro_noise(float2 p)
{
    float2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(macro_hash(i), macro_hash(i + float2(1, 0)), f.x), mix(macro_hash(i + float2(0, 1)), macro_hash(i + float2(1, 1)), f.x), f.y);
}
static inline float macro_variation(float2 world_xz)
{
    float n = 0.65 * macro_noise(world_xz * 0.025) + 0.35 * macro_noise(world_xz * 0.11 + 7.3);
    return 0.91 + 0.18 * n;
}

fragment float4 PSMain(VSOut in [[stage_in]],
                       constant PerFrame& frame [[buffer(0)]],
                       constant PerObject& obj [[buffer(1)]],
                       constant LightBuffer& lights [[buffer(2)]],
                       texture2d<float> albedo_tex    [[texture(0)]], sampler albedo_smp    [[sampler(0)]],
                       texture2d<float> normal_tex    [[texture(1)]], sampler normal_smp    [[sampler(1)]],
                       texture2d<float> splat_tex     [[texture(2)]], sampler splat_smp     [[sampler(2)]],
                       texture2d<float> roughness_tex [[texture(3)]], sampler roughness_smp [[sampler(3)]],
                       texture2d<float> ao_tex        [[texture(4)]], sampler ao_smp        [[sampler(4)]],
                       texture2d<float> height_tex    [[texture(5)]], sampler height_smp    [[sampler(5)]],
                       depth2d<float>   spot_shadow   [[texture(6)]], sampler shadow_smp    [[sampler(6)]],
                       depth2d<float>   csm_shadow    [[texture(7)]], sampler csm_smp       [[sampler(7)]],
                       depth2d<float>   point_shadow  [[texture(8)]], sampler point_smp     [[sampler(8)]],
                       texture2d<float> ssao_tex      [[texture(9)]], sampler ssao_smp      [[sampler(9)]])
{
    float3 cam_pos = float3(frame.camera_position);
    float3 Ng = normalize(in.norm);

    float4 w = obj.has_metallic_texture != 0 ? splat_tex.sample(splat_smp, in.uv) : float4(1.0, 0.0, 0.0, 0.0);
    float wsum = w.r + w.g + w.b + w.a;
    w = wsum > 1e-4 ? w / wsum : float4(1.0, 0.0, 0.0, 0.0);

    float2 p = in.world_pos.xz;
    float4 tiles_per_metre = max(obj.base_color, float4(1e-4));
    float2 dpx = dfdx(p), dpy = dfdy(p);

    float3 albedo = float3(0.0);
    float3 nm = float3(0.0);
    float rough = 0.0;
    // detail: the layer's own normal map again at 7.3x finer tiling, fading out beyond ~28 m — close-up ripples and grain
    float detail_w = saturate(1.0 - length(float3(frame.camera_position) - in.world_pos) / 28.0);
    for (int i = 0; i < 4; ++i)
    {
        float wi = w[i];
        if (wi <= 0.002) continue;
        float2 uv = p * tiles_per_metre[i];
        float2 a = atlas_uv(uv, i);
        float2 dx = dpx * tiles_per_metre[i] * 0.5, dy = dpy * tiles_per_metre[i] * 0.5;
        clamp_grad(dx, dy);
        albedo += wi * (obj.has_albedo_texture != 0 ? srgb_to_linear(albedo_tex.sample(albedo_smp, a, gradient2d(dx, dy)).rgb) : float3(0.5));
        if (obj.has_normal_texture != 0) nm += wi * (normal_tex.sample(normal_smp, a, gradient2d(dx, dy)).rgb * 2.0 - 1.0);
        if (obj.has_normal_texture != 0 && detail_w > 0.001)
        {
            float2 ad = atlas_uv(uv * 7.3 + 0.37, i);
            float2 dxd = dx * 7.3, dyd = dy * 7.3;
            clamp_grad(dxd, dyd);
            float3 nd = normal_tex.sample(normal_smp, ad, gradient2d(dxd, dyd)).rgb * 2.0 - 1.0;
            nm += wi * detail_w * 0.6 * float3(nd.xy, 0.0);
        }
        if (obj.has_roughness_texture != 0) rough += wi * roughness_tex.sample(roughness_smp, a, gradient2d(dx, dy)).r;
    }
    albedo *= in.tint.rgb;
    albedo *= macro_variation(in.world_pos.xz);
    float roughness = max(obj.has_roughness_texture != 0 ? rough : obj.roughness, 0.04);
    float metallic = 0.0;
    float ao = obj.ao;

    float3 N = Ng;
    if (obj.has_normal_texture != 0 && dot(nm, nm) > 1e-6)
    {
        if (obj.use_directx_normals == 0) nm.y = -nm.y;
        nm.xy *= obj.normal_strength;
        float3 T = normalize(float3(1.0, 0.0, 0.0) - Ng * Ng.x);
        float3 B = normalize(float3(0.0, 0.0, 1.0) - Ng * Ng.z);
        N = normalize(nm.x * T + nm.y * B + max(nm.z, 0.05) * Ng);
    }

    float3 V = normalize(cam_pos - in.world_pos);
    float NdotV = max(dot(N, V), 0.001);
    float3 F0 = float3(0.04);
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
        Lo += (kD * albedo / PI + spec) * radiance * NdotL * sun_shadow;
    }

    for (uint li = 0; li < frame.point_light_count && li < MAX_POINT_LIGHTS; ++li)
    {
        float3 lpos = float3(lights.point_lights[li].position);
        float3 light_vec = lpos - in.world_pos;
        float dist = length(light_vec);
        if (dist < lights.point_lights[li].range)
        {
            float3 L = light_vec / dist;
            float3 H = normalize(V + L);
            float NdotL = max(dot(N, L), 0.0);
            float NdotH = max(dot(N, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);
            float atten = attenuation(dist, lights.point_lights[li].range);
            atten *= sample_point_shadow(lights, point_shadow, point_smp, in.world_pos, lpos, (int)li);
            float3 radiance = float3(lights.point_lights[li].color) * lights.point_lights[li].intensity * atten;
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
    float3 ambient = hemisphere * frame.ambient_strength * albedo * ao;

    float3 R = reflect(-V, N);
    float3 env_color;
    if (frame.env_sky.w > 0.5)
    {
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
    ambient += env_color * env_fresnel * ao;

    if (frame.ssao_enabled > 0.5)
    {
        float2 auv = in.pos.xy * float2(frame.ssao_padding[0], frame.ssao_padding[1]);
        ambient *= ssao_tex.sample(ssao_smp, auv).r;
    }

    float3 color = ambient + Lo;
    color = apply_fog(frame, color, in.world_pos);

    float3 x = color * 0.5;
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    color = saturate(a / b);
    color = pow(max(color, 0.0), 1.0 / 2.2);
    return float4(color, 1.0);
}
