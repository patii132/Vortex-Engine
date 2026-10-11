// Water shader (#200) — Metal twin of water.hlsl: lakes, ponds, pools through the custom material-shader route
// (alpha-blended: the sorted transparent pass). The surface mesh carries the ground depth in uv.x; see water.hlsl for
// the per-material inputs (base_color deep + reflection, tint shallow, normal_strength absorption, uv_tiling wave
// scale / speed, alpha_cutoff wave height, height_scale time, emissive_strength foam width, roughness).
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
    float4 camera_clip;   // x near, y far clip of this view (#200 refraction / SSR) — @240
};

struct PerObject
{
    float4x4 world;
    float4 base_color;
    float metallic;
    float roughness;
    float ao;
    float normal_strength;
    uint has_albedo_texture;
    uint has_normal_texture;
    uint has_metallic_texture;
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

struct PointLight { packed_float3 position; float range; packed_float3 color; float intensity; };
struct SpotLight
{
    packed_float3 position; float range; packed_float3 direction; float spot_angle; packed_float3 color; float intensity;
    float inner_spot_angle; float shadow_strength; float shadow_bias; float shadow_slot;
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
    o.tangent = float3(1.0, 0.0, 0.0);
    o.bitangent = float3(0.0, 0.0, 1.0);
    o.tint = tint;
    return o;
}

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

static inline float sample_cascade_shadow(constant PerFrame& frame, constant LightBuffer& lights, depth2d<float> atlas, sampler smp, float3 wp)
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

static inline float3 wave_normal(float2 p, float t, float height)
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

static inline float hash2(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
static inline float noise2(float2 p)
{
    float2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash2(i), hash2(i + float2(1, 0)), f.x), mix(hash2(i + float2(0, 1)), hash2(i + float2(1, 1)), f.x), f.y);
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
static inline float3 sample_env(texture2d<float> env_tex, sampler env_smp, float3 R, float roughness, float mips, float lod_min)
{
    float lod = max(saturate(roughness) * max(mips - 1.0, 0.0), lod_min);
    return decode_env(env_tex.sample(env_smp, env_uv(R), level(lod)).rgb);
}

// Window z -> view-space distance for this view's perspective projection (near / far in camera_clip).
static inline float linear_depth(float z, float4 clip)
{
    float n = clip.x, f = clip.y;
    return n * f / max(f - z * (f - n), 1e-4);
}

// Screen-space reflection (#200): the reflected view ray, projected to the picture and walked there in even steps
// (1 / w interpolates linearly along a straight line, so each sample's view depth is exact); where the ray dips behind
// the opaque depth copy by less than a thickness that grows with distance, a few bisection steps settle the hit. The
// hit's screen UV comes back with a weight that fades at the picture's edges; 0 = nothing (the sky map takes over).
static inline float ssr_trace(constant PerFrame& frame, depth2d<float> scene_depth, float3 origin, float3 dir, float cam_dist, thread float2& hit_uv)
{
    hit_uv = float2(0.0, 0.0);
    // orthographic views (the w column of a perspective view-projection carries the forward vector, an orthographic one
    // is all zero there) and rays into the water: nothing
    if (abs(frame.view_projection[0][3]) + abs(frame.view_projection[1][3]) + abs(frame.view_projection[2][3]) < 1e-5 || dir.y <= 0.02) return 0.0;
    float2 dim = float2(scene_depth.get_width(), scene_depth.get_height());
    float4 c0 = frame.view_projection * float4(origin, 1.0);
    float4 cd = frame.view_projection * float4(dir, 0.0);
    float L = 300.0;
    if (c0.w + L * cd.w < 0.2) L = (0.2 - c0.w) / cd.w;   // towards the camera: stop in front of the near plane
    if (L <= 0.05) return 0.0;
    float4 c1 = c0 + L * cd;
    float2 uv0 = float2(0.5 + 0.5 * c0.x / c0.w, 0.5 - 0.5 * c0.y / c0.w);
    float2 uv1 = float2(0.5 + 0.5 * c1.x / c1.w, 0.5 - 0.5 * c1.y / c1.w);
    float2 d = uv1 - uv0;
    float tEnd = 1.0;
    if (d.x > 0.0) tEnd = min(tEnd, (1.0 - uv0.x) / d.x); else if (d.x < 0.0) tEnd = min(tEnd, -uv0.x / d.x);
    if (d.y > 0.0) tEnd = min(tEnd, (1.0 - uv0.y) / d.y); else if (d.y < 0.0) tEnd = min(tEnd, -uv0.y / d.y);
    if (tEnd <= 0.0) return 0.0;
    float invW0 = 1.0 / c0.w, invW1 = 1.0 / c1.w;
    float tPrev = 0.0;
    for (int i = 1; i <= 48; ++i)
    {
        float t = tEnd * float(i) / 48.0;
        float2 uv = uv0 + d * t;
        float w = 1.0 / mix(invW0, invW1, t);
        float zs = linear_depth(scene_depth.read(uint2(uv * dim)), frame.camera_clip);
        if (zs < w - 0.02 && w - zs < max(0.4, 0.06 * w))
        {
            float a = tPrev, b = t;
            for (int k = 0; k < 4; ++k)
            {
                float m = 0.5 * (a + b);
                float wm = 1.0 / mix(invW0, invW1, m);
                float zm = linear_depth(scene_depth.read(uint2((uv0 + d * m) * dim)), frame.camera_clip);
                if (zm < wm) b = m; else a = m;
            }
            float2 uvh = uv0 + d * b;
            hit_uv = uvh;
            float2 e = min(uvh, 1.0 - uvh);
            return saturate(min(e.x, e.y) * 10.0);
        }
        tPrev = t;
    }
    return 0.0;
}

fragment float4 PSMain(VSOut in [[stage_in]],
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
                       texture2d<float> env_tex       [[texture(10)]], sampler env_smp       [[sampler(10)]],
                       texture2d<float> scene_color   [[texture(11)]], sampler scene_smp     [[sampler(11)]],   // the opaque colour so far (#200)
                       depth2d<float>   scene_depth   [[texture(12)]], sampler depth_smp     [[sampler(12)]])   // + its depth (window z)
{
    float depth = max(in.uv.x, 0.0);
    float3 deep = obj.base_color.rgb, shallow = in.tint.rgb;
    float reflection = saturate(obj.base_color.a);
    float absorb = max(obj.normal_strength, 0.1);
    float wave_scale = max(obj.uv_tiling.x, 0.1), wave_speed = max(obj.uv_tiling.y, 0.0);
    float t = obj.height_scale * wave_speed;
    float foam_width = obj.emissive_strength;
    float rough = clamp(obj.roughness, 0.01, 1.0);
    float3 cam_pos = float3(frame.camera_position);

    float2 p = in.world_pos.xz / wave_scale;
    float3 N = wave_normal(p, t, obj.alpha_cutoff);
    // the ripples fade with distance: beyond ~80 m the per-pixel sine slopes only alias into stripes
    float cam_dist = length(cam_pos - in.world_pos);
    N = normalize(mix(float3(0.0, 1.0, 0.0), N, saturate(80.0 / max(cam_dist, 1.0))));
    float3 V = normalize(cam_pos - in.world_pos);
    float NdotV = max(dot(N, V), 0.0);
    float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

    float body = 1.0 - exp(-depth / absorb);
    float3 water = mix(shallow, deep, body);

    float3 sky_color = frame.env_sky.w > 0.5 ? frame.env_sky.rgb : float3(0.5, 0.55, 0.7);
    float3 horizon = frame.env_sky.w > 0.5 ? frame.env_horizon.rgb : float3(0.6, 0.62, 0.68);
    float3 L = normalize(-float3(frame.light_direction));
    float shadow = sample_cascade_shadow(frame, lights, csm_shadow, csm_smp, in.world_pos);
    float3 sun = float3(frame.light_color) * frame.directional_intensity * shadow;
    float3 lit = water * (horizon * frame.ambient_strength * 1.2 + sun * (0.12 + 0.28 * saturate(dot(N, L))));

    float3 R = reflect(-V, N);
    const bool have_scene = scene_color.get_width() > 1;   // the white 1 px texture stands in before the copies exist
    float2 ssr_uv = float2(0.0, 0.0);
    float ssr_w = have_scene ? ssr_trace(frame, scene_depth, in.world_pos, R, cam_dist, ssr_uv) : 0.0;
    float3 sky_refl;
    if (frame.env_sky.w > 1.5)
    {
        // the real sky in the water: never below the horizon (the picture's ground is not the lake's surroundings), and
        // blurred with distance — far away the per-pixel ripple slopes would only turn the sky's sharp horizon into stripes
        float lod_min = 4.0 * saturate((cam_dist - 40.0) / 160.0);
        sky_refl = sample_env(env_tex, env_smp, float3(R.x, max(R.y, 0.02), R.z), rough, frame.env_horizon.w, lod_min);
    }
    else if (frame.env_sky.w > 0.5)
        sky_refl = R.y >= 0.0 ? mix(frame.env_horizon.rgb, frame.env_sky.rgb, pow(saturate(R.y), 0.5)) : mix(frame.env_horizon.rgb, frame.env_ground.rgb, pow(saturate(-R.y), 0.6));
    else
        sky_refl = mix(horizon, sky_color, saturate(R.y)) * 0.8;
    sky_refl *= max(frame.ambient_strength, 0.3) * 1.3;
    float3 color = mix(lit, sky_refl, fresnel * reflection * (1.0 - ssr_w));

    float3 H = normalize(V + L);
    float spec_pow = mix(1400.0, 24.0, rough);
    float spec = pow(max(dot(N, H), 0.0), spec_pow) * (spec_pow + 8.0) / (8.0 * PI) * 0.06;
    color += sun * spec * mix(0.4, 1.0, fresnel);

    float foam = 0.0;
    if (foam_width > 0.0)
    {
        float n = noise2(p * 6.0 + float2(t * 0.35, -t * 0.2)) * 0.6 + noise2(p * 17.0 - float2(t * 0.5, t * 0.3)) * 0.4;
        float band = saturate(1.0 - depth / foam_width);
        foam = saturate(band * band * (0.35 + 0.9 * n) * 1.4);
    }
    float3 foam_color = float3(0.92, 0.94, 0.92) * (horizon * frame.ambient_strength + sun * 0.9);
    color = mix(color, foam_color, foam * 0.85);

    float alpha = saturate(depth / 0.3) * mix(0.5, 0.97, saturate(depth / absorb));
    alpha = max(alpha, foam * 0.95 * saturate(depth / 0.08));

    color = apply_fog(frame, color, in.world_pos);
    float3 x = color * 0.5;
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    color = saturate(a / b);
    color = pow(max(color, 0.0), 1.0 / 2.2);
    if (have_scene)
    {
        // Refraction (#200): the opaque picture behind the surface, bent by the ripples (less in the shallows and far away),
        // unless the bent sample lies in front of the surface — then the straight one. Composed here instead of the blend,
        // and the screen-space reflection laid over it where the march hit.
        float2 dim = float2(scene_color.get_width(), scene_color.get_height());
        float2 uv = in.pos.xy / dim;
        float2 ruv = clamp(uv + N.xz * 0.12 * saturate(depth / 0.6) * saturate(40.0 / max(cam_dist, 1.0)), 0.5 / dim, 1.0 - 0.5 / dim);
        if (scene_depth.read(uint2(ruv * dim)) < in.pos.z) ruv = uv;
        float3 behind = scene_color.sample(scene_smp, ruv, level(0)).rgb;
        float3 outc = mix(behind, color, alpha);
        if (ssr_w > 0.0)
            outc = mix(outc, scene_color.sample(scene_smp, ssr_uv, level(0)).rgb, ssr_w * fresnel * reflection * (1.0 - foam) * alpha);
        return float4(outc, 1.0);
    }
    return float4(color, alpha);
}
