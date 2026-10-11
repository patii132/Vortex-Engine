// Particles for the SDL GPU backend (VFX epic #116): camera-facing / stretched / flat billboards pulled from a
// storage buffer (6 vertices per instance), ribbons (trails + beams) from a vertex buffer, and the linear-depth
// snapshot the CPU simulation collides against. Drawn after the opaque + transparent meshes of a layer in a pass
// WITHOUT a depth attachment: the scene depth is sampled instead, which gives both the depth test (discard) and
// soft particles (fade near geometry) without copying the depth buffer.
//
// Binding conventions follow SDL GPU's MSL rules (see standard.metal):
//   vertex:   uniform buffers [[buffer(0..2)]] (frame, batch, lights), storage buffer [[buffer(3)]] (instances)
//   fragment: uniform buffers [[buffer(0..1)]]; texture 0 = particle texture, texture 1 = scene depth
// GpuParticle / PFrame / PBatch are byte-matched to the C++ structs (ParticleSystem.h, SdlGpuParticles.cpp).
#include <metal_stdlib>
using namespace metal;

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359
#define MODE_RIBBON 16u

struct PFrame
{
    float4x4 view_projection;   // @0   (row-major in memory -> M * v here)
    float4 cam_right;           // @64
    float4 cam_up;              // @80
    float4 cam_forward;         // @96
    float4 eye;                 // @112
    float4 depth_params;        // @128 x near, y far, z ortho
    float4 fog;                 // @144 rgb, density
    float4 fog2;                // @160 height_y, height_falloff
    float4 sun_dir;             // @176 xyz = direction the light travels, w = intensity
    float4 sun_color;           // @192 rgb, w = ambient strength
    uint4 counts;               // @208 x = point lights, y = spot lights
};

struct PBatch
{
    uint base;                  // first instance of this batch in the storage buffer
    uint mode;                  // 0 billboard, 1 stretched, 2 horizontal, 3 vertical, 16 ribbon
    uint tiles_x, tiles_y;
    uint frame_blend, lit, blend, has_texture;   // blend: 0 alpha, 1 additive, 2 premultiplied
    float soft_inv;             // 1 / soft distance (0 = hard)
    float emissive;
    float pad0, pad1;
};

struct PointLight { packed_float3 position; float range; packed_float3 color; float intensity; };
struct SpotLight
{
    packed_float3 position;  float range;
    packed_float3 direction; float spot_angle;
    packed_float3 color;     float intensity;
    float inner_spot_angle;  float shadow_strength; float shadow_bias; float shadow_slot;
};
struct PLights { PointLight point_lights[MAX_POINT_LIGHTS]; SpotLight spot_lights[MAX_SPOT_LIGHTS]; };   // 1024 bytes

struct GpuParticle
{
    packed_float3 pos;  float size;
    packed_float3 axis; float rot;
    uint color;         float frame;  float aspect; uint flags;
};

struct PVOut
{
    float4 pos [[position]];
    float2 uv0;
    float2 uv1;
    float2 local;       // quad-local [-1,1] (procedural disc) / ribbon: y = across
    float blend_t;
    float view_z;
    float fog;
    float4 color;
    float3 light;
};

static inline float4 unpack_color(uint c)
{
    return float4(float(c & 255u), float((c >> 8) & 255u), float((c >> 16) & 255u), float(c >> 24)) * (1.0 / 255.0);
}

static inline float fog_amount(constant PFrame& f, float3 wp)
{
    if (f.fog.w <= 0.0) return 0.0;
    // the standard shader's height fog (#328): uniform up to fog2.x, exp(-fog2.y * height) above, integrated along the ray
    float3 cam = f.eye.xyz;
    float dist = length(wp - cam);
    float density = f.fog.w, height_y = f.fog2.x, k = f.fog2.y;
    float optical;
    if (k <= 0.0) { float d = density * dist; optical = -log2(max(exp2(-d * d), 1e-6)); }
    else
    {
        float ya = cam.y - height_y, yb = wp.y - height_y;
        if (ya <= 0.0 && yb <= 0.0) optical = density * dist;
        else
        {
            float t0 = 0.0, t1 = 1.0;
            if (ya <= 0.0) t0 = -ya / (yb - ya);
            else if (yb <= 0.0) t1 = ya / (ya - yb);
            float above = (t1 - t0) * dist, below = dist - above;
            float hya = max(ya, 0.0), hyb = max(yb, 0.0), dyv = hyb - hya;
            float mean = abs(dyv) > 1e-3 ? (exp(-k * hya) - exp(-k * hyb)) / (k * dyv) : exp(-k * hya);
            optical = density * (below + above * mean);
        }
    }
    return saturate(1.0 - exp2(-optical));
}

static inline float atten(float dist, float range)
{
    float d = dist / range;
    float a = saturate(1.0 - d * d);
    return a * a / (dist * dist + 0.01);
}

// Volumetric-ish lighting at the particle centre: hemisphere ambient + wrapped sun + point / spot lights without N·L.
static float3 particle_light(constant PFrame& f, constant PLights& L, float3 wp)
{
    float3 amb = mix(float3(0.15, 0.15, 0.18), float3(0.5, 0.55, 0.7), 0.6) * f.sun_color.w;
    float3 N = -f.cam_forward.xyz;
    float3 Ld = normalize(-f.sun_dir.xyz);
    float wrap = saturate(dot(N, Ld) * 0.5 + 0.5);
    float3 c = amb + f.sun_color.rgb * f.sun_dir.w * wrap * (1.0 / PI) * 2.0;
    for (uint i = 0; i < f.counts.x && i < MAX_POINT_LIGHTS; ++i)
    {
        float3 lv = float3(L.point_lights[i].position) - wp;
        float dist = length(lv);
        if (dist < L.point_lights[i].range)
            c += float3(L.point_lights[i].color) * L.point_lights[i].intensity * atten(dist, L.point_lights[i].range);
    }
    for (uint j = 0; j < f.counts.y && j < MAX_SPOT_LIGHTS; ++j)
    {
        float3 lv = float3(L.spot_lights[j].position) - wp;
        float dist = length(lv);
        if (dist < L.spot_lights[j].range && dist > 1e-4)
        {
            float3 Lj = lv / dist;
            float theta = dot(-Lj, normalize(float3(L.spot_lights[j].direction)));
            float oc = cos(L.spot_lights[j].spot_angle * 0.5 * PI / 180.0);
            float ic = cos(L.spot_lights[j].inner_spot_angle * 0.5 * PI / 180.0);
            float fade = saturate((theta - oc) / (ic - oc + 0.001));
            c += float3(L.spot_lights[j].color) * L.spot_lights[j].intensity * atten(dist, L.spot_lights[j].range) * fade;
        }
    }
    return c;
}

static inline float2 tile_uv(float2 uv, float frame, uint tx, uint ty)
{
    uint frames = tx * ty;
    uint f = uint(frame) % max(frames, 1u);
    float2 cell = float2(float(f % tx), float(f / tx));
    return (uv + cell) / float2(float(tx), float(ty));
}

vertex PVOut ParticleVS(uint vid [[vertex_id]], uint iid [[instance_id]],
                        constant PFrame& f [[buffer(0)]], constant PBatch& b [[buffer(1)]],
                        constant PLights& L [[buffer(2)]], const device GpuParticle* parts [[buffer(3)]])
{
    const float2 corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };
    GpuParticle p = parts[b.base + iid];
    float2 c = corners[vid % 6];
    float3 center = float3(p.pos);
    float half_h = p.size * 0.5;
    float half_w = half_h * p.aspect;
    float s = sin(p.rot), co = cos(p.rot);
    float2 rc = float2(c.x * co - c.y * s, c.x * s + c.y * co);
    float3 wp;
    if (b.mode == 1u)
    {
        // stretched along the velocity: the streak vector is in axis, width = size
        float3 axis = float3(p.axis);
        float3 dir = normalize(axis + float3(0, 1e-6, 0));
        float3 tocam = f.depth_params.z > 0.5 ? -f.cam_forward.xyz : (f.eye.xyz - center);
        float3 side = cross(dir, tocam);
        float sl = length(side);
        side = sl > 1e-6 ? side / sl : f.cam_right.xyz;
        wp = center + side * (c.x * half_w) + axis * (c.y * 0.5);
    }
    else if (b.mode == 2u)
    {
        // horizontal (lies on the XZ plane: ground ripples, decals-like splats)
        wp = center + float3(rc.x * half_w, 0.0, rc.y * half_h);
    }
    else if (b.mode == 3u)
    {
        // vertical (Y-axis aligned, faces the camera around Y: fire sheets)
        float3 tocam = f.eye.xyz - center; tocam.y = 0.0;
        float tl = length(tocam);
        float3 fwd = tl > 1e-5 ? tocam / tl : float3(0, 0, -1);
        float3 right = normalize(cross(float3(0, 1, 0), fwd));
        wp = center + right * (rc.x * half_w) + float3(0, 1, 0) * (rc.y * half_h);
    }
    else
    {
        wp = center + f.cam_right.xyz * (rc.x * half_w) + f.cam_up.xyz * (rc.y * half_h);
    }

    PVOut o;
    o.pos = f.view_projection * float4(wp, 1.0);
    float2 uv = float2(c.x * 0.5 + 0.5, 0.5 - c.y * 0.5);
    o.uv0 = tile_uv(uv, p.frame, b.tiles_x, b.tiles_y);
    o.uv1 = tile_uv(uv, p.frame + 1.0, b.tiles_x, b.tiles_y);
    o.blend_t = fract(p.frame);
    o.local = c;
    o.view_z = dot(wp - f.eye.xyz, f.cam_forward.xyz);
    o.color = unpack_color(p.color);
    o.fog = fog_amount(f, center);
    o.light = b.lit != 0u ? particle_light(f, L, center) : float3(1.0);
    return o;
}

struct RibbonIn
{
    float3 pos   [[attribute(0)]];
    float2 uv    [[attribute(1)]];
    float4 color [[attribute(2)]];
};

vertex PVOut RibbonVS(RibbonIn in [[stage_in]], constant PFrame& f [[buffer(0)]], constant PBatch& b [[buffer(1)]],
                      constant PLights& L [[buffer(2)]])
{
    PVOut o;
    o.pos = f.view_projection * float4(in.pos, 1.0);
    o.uv0 = in.uv;
    o.uv1 = in.uv;
    o.blend_t = 0.0;
    o.local = float2(0.0, in.uv.y * 2.0 - 1.0);
    o.view_z = dot(in.pos - f.eye.xyz, f.cam_forward.xyz);
    o.color = in.color;
    o.fog = fog_amount(f, in.pos);
    o.light = b.lit != 0u ? particle_light(f, L, in.pos) : float3(1.0);
    return o;
}

static inline float linear_depth(constant PFrame& f, float d)
{
    float n = f.depth_params.x, fa = f.depth_params.y;
    if (f.depth_params.z > 0.5) return n + d * (fa - n);
    return (n * fa) / max(fa - d * (fa - n), 1e-6);
}

static inline float3 aces(float3 color, float exposure, uint mode)
{
    float3 x = color * (exposure > 0.0 ? exposure : 0.5);   // the surfaces' scene exposure (fog2.z)
    if (mode == 1u)
    {
        // Khronos PBR Neutral (the scene's tonemapper 1): hue-preserving — see standard.metal
        const float start_compression = 0.8 - 0.04;
        const float desaturation = 0.15;
        float mn = min(x.r, min(x.g, x.b));
        float offset = mn < 0.08 ? mn - 6.25 * mn * mn : 0.04;
        x -= offset;
        float peak = max(x.r, max(x.g, x.b));
        if (peak < start_compression) return saturate(x);
        const float d = 1.0 - start_compression;
        float new_peak = 1.0 - d * d / (peak + d - start_compression);
        x *= new_peak / peak;
        float g = 1.0 - 1.0 / (desaturation * (peak - new_peak) + 1.0);
        return saturate(mix(x, float3(new_peak), g));
    }
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    return saturate(a / b);
}

fragment float4 ParticlePS(PVOut in [[stage_in]], constant PFrame& f [[buffer(0)]], constant PBatch& b [[buffer(1)]],
                           texture2d<float> tex [[texture(0)]], sampler smp [[sampler(0)]],
                           depth2d<float> depth [[texture(1)]], sampler dsmp [[sampler(1)]])
{
    float4 t;
    if (b.has_texture != 0u)
    {
        t = tex.sample(smp, in.uv0);
        if (b.frame_blend != 0u) t = mix(t, tex.sample(smp, in.uv1), in.blend_t);
    }
    else
    {
        // no texture: a soft round puff (billboards) / a soft-edged strip (ribbons)
        float r = b.mode == MODE_RIBBON ? abs(in.local.y) : length(in.local);
        float a = saturate(1.0 - r);
        a = a * a * (3.0 - 2.0 * a);
        t = float4(1.0, 1.0, 1.0, a);
    }
    float4 c = t * in.color;
    if (b.blend == 2u) c.rgb *= in.color.a;   // premultiplied: the tint's alpha scales the colour too

    // depth test + soft particles against this pass's scene depth (no depth attachment is bound)
    uint2 px = uint2(max(in.pos.xy, float2(0.0)));
    px = min(px, uint2(depth.get_width() - 1, depth.get_height() - 1));
    float scene_z = linear_depth(f, depth.read(px));
    float dz = scene_z - in.view_z;
    if (dz < 0.0) discard_fragment();
    float fade = b.soft_inv > 0.0 ? saturate(dz * b.soft_inv) : 1.0;
    fade *= saturate((in.view_z - f.depth_params.x) * 5.0);   // no pop at the near plane

    if (b.lit != 0u)
    {
        float3 lin = pow(max(c.rgb, 0.0), 2.2) * in.light;
        c.rgb = pow(aces(lin, f.fog2.z, f.counts.z), 1.0 / 2.2);
    }
    c.rgb *= b.emissive;
    if (b.blend == 1u) c.rgb *= (1.0 - in.fog);                      // additive glows vanish into the fog
    else if (b.blend == 2u) c.rgb = mix(c.rgb, f.fog.rgb * c.a, in.fog);
    else c.rgb = mix(c.rgb, f.fog.rgb, in.fog);
    c.a *= fade;
    if (b.blend == 2u) c.rgb *= fade;
    if (c.a <= 0.001 && b.blend != 2u) discard_fragment();
    return c;
}

// ---- collision snapshot: scene depth -> linear view depth (metres), downsampled (point) ----
struct SnapCB
{
    float4 depth_params;    // x near, y far, z ortho
    float2 src_size;
    float2 dst_size;
};

struct SnapOut
{
    float4 pos [[position]];
    float2 uv;
};

vertex SnapOut SnapVS(uint id [[vertex_id]])
{
    SnapOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

fragment float4 SnapPS(SnapOut in [[stage_in]], constant SnapCB& c [[buffer(0)]],
                       depth2d<float> depth [[texture(0)]], sampler smp [[sampler(0)]])
{
    float2 p = clamp(floor(in.uv * c.src_size), float2(0.0), c.src_size - 1.0);
    float d = depth.read(uint2(p));
    float n = c.depth_params.x, fa = c.depth_params.y;
    float z = c.depth_params.z > 0.5 ? n + d * (fa - n) : (n * fa) / max(fa - d * (fa - n), 1e-6);
    return float4(z, 0.0, 0.0, 1.0);
}
