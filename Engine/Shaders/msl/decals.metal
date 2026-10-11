// Projected decals for the SDL GPU backend (#120) — the Metal twin of decals.hlsl / the GLSL decals.* set.
// Each decal is a unit box drawn as its BACK faces (36 vertices per instance from a storage buffer, no vertex
// buffer, front faces culled, no depth attachment): the fragment shader reads the scene depth, reconstructs the
// covered position, rejects pixels outside the box and blends the material over the scene — lit, multiplied, or added.
//
// Binding conventions follow SDL GPU's MSL rules (see standard.metal):
//   vertex:   uniform buffers [[buffer(0..1)]] (frame, batch), storage buffer [[buffer(2)]] (decals)
//   fragment: uniform buffers [[buffer(0..2)]] (frame, batch, lights); texture 0 = decal texture, texture 1 = scene depth
// DFrame / DBatch / GpuDecal are byte-matched to the C++ structs (SdlGpuDecals.cpp, Graphics/Decals/Decals.h).
#include <metal_stdlib>
using namespace metal;

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

struct DFrame
{
    float4x4 view_projection;       // @0   (row-major in memory -> M * v here)
    float4x4 inv_view_projection;   // @64
    float4 eye;                     // @128
    float4 screen;                  // @144 w, h, 1/w, 1/h
    float4 depth_params;            // @160 x near, y far, z ortho
    float4 fog;                     // @176 rgb, density
    float4 fog2;                    // @192 height_y, height_falloff
    float4 sun_dir;                 // @208 xyz = direction the light travels, w = intensity
    float4 sun_color;               // @224 rgb, w = ambient strength
    uint4 counts;                   // @240 x = point lights, y = spot lights
};                                  // = 256

struct DBatch
{
    uint base;                      // first instance of this batch
    uint blend;                     // 0 lit, 1 multiply, 2 additive
    uint has_texture;
    uint pad;
    float4 base_color;              // the material's base colour (× the decal tint)
};                                  // = 32

struct PointLight { packed_float3 position; float range; packed_float3 color; float intensity; };
struct SpotLight
{
    packed_float3 position;  float range;
    packed_float3 direction; float spot_angle;
    packed_float3 color;     float intensity;
    float inner_spot_angle;  float shadow_strength; float shadow_bias; float shadow_slot;
};
struct DLights { PointLight point_lights[MAX_POINT_LIGHTS]; SpotLight spot_lights[MAX_SPOT_LIGHTS]; };   // 1024 bytes

struct GpuDecal
{
    float4x4 world;                 // unit box -> world
    float4x4 inv_world;             // world -> unit box
    float4 color;                   // tint rgb + opacity
    float4 params;                  // x angle fade, y fade distance
};                                  // 160 bytes

struct DVOut
{
    float4 pos [[position]];
    float4 inv0 [[flat]];
    float4 inv1 [[flat]];
    float4 inv2 [[flat]];
    float4 inv3 [[flat]];
    float3 axis [[flat]];
    float4 color [[flat]];
    float4 params [[flat]];
};

// clockwise from outside — the same table as decals.hlsl
constant float3 CUBE[36] = {
    float3(-0.5, -0.5, -0.5), float3(-0.5,  0.5, -0.5), float3( 0.5,  0.5, -0.5),
    float3(-0.5, -0.5, -0.5), float3( 0.5,  0.5, -0.5), float3( 0.5, -0.5, -0.5),
    float3(-0.5, -0.5,  0.5), float3( 0.5, -0.5,  0.5), float3( 0.5,  0.5,  0.5),
    float3(-0.5, -0.5,  0.5), float3( 0.5,  0.5,  0.5), float3(-0.5,  0.5,  0.5),
    float3(-0.5, -0.5, -0.5), float3(-0.5, -0.5,  0.5), float3(-0.5,  0.5,  0.5),
    float3(-0.5, -0.5, -0.5), float3(-0.5,  0.5,  0.5), float3(-0.5,  0.5, -0.5),
    float3( 0.5, -0.5, -0.5), float3( 0.5,  0.5, -0.5), float3( 0.5,  0.5,  0.5),
    float3( 0.5, -0.5, -0.5), float3( 0.5,  0.5,  0.5), float3( 0.5, -0.5,  0.5),
    float3(-0.5, -0.5, -0.5), float3( 0.5, -0.5, -0.5), float3( 0.5, -0.5,  0.5),
    float3(-0.5, -0.5, -0.5), float3( 0.5, -0.5,  0.5), float3(-0.5, -0.5,  0.5),
    float3(-0.5,  0.5, -0.5), float3(-0.5,  0.5,  0.5), float3( 0.5,  0.5,  0.5),
    float3(-0.5,  0.5, -0.5), float3( 0.5,  0.5,  0.5), float3( 0.5,  0.5, -0.5),
};

vertex DVOut DecalVS(uint vid [[vertex_id]], uint iid [[instance_id]],
                     constant DFrame& f [[buffer(0)]], constant DBatch& b [[buffer(1)]],
                     const device GpuDecal* decals [[buffer(2)]])
{
    GpuDecal d = decals[b.base + iid];
    float3 wp = (d.world * float4(CUBE[vid % 36], 1.0)).xyz;
    DVOut o;
    o.pos = f.view_projection * float4(wp, 1.0);
    o.inv0 = d.inv_world[0]; o.inv1 = d.inv_world[1]; o.inv2 = d.inv_world[2]; o.inv3 = d.inv_world[3];
    o.axis = normalize(d.world[1].xyz);   // the box's local +Y in world space
    o.color = d.color;
    o.params = d.params;
    return o;
}

static float p_atten(float dist, float range)
{
    float d = dist / range;
    float a = saturate(1.0 - d * d);
    return a * a / (dist * dist + 0.01);
}

static float fog_amount(constant DFrame& f, float3 wp)
{
    if (f.fog.w <= 0.0) return 0.0;
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
            float mean_density = abs(dyv) > 1e-3 ? (exp(-k * hya) - exp(-k * hyb)) / (k * dyv) : exp(-k * hya);
            optical = density * (below + above * mean_density);
        }
    }
    return saturate(1.0 - exp2(-optical));
}

static float3 decal_light(constant DFrame& f, constant DLights& L, float3 wp, float3 n)
{
    float3 amb = mix(float3(0.15, 0.15, 0.18), float3(0.5, 0.55, 0.7), 0.6) * f.sun_color.w;
    float3 Ld = normalize(-f.sun_dir.xyz);
    float3 c = amb + f.sun_color.rgb * f.sun_dir.w * saturate(dot(n, Ld)) * (1.0 / PI) * 2.0;
    for (uint i = 0u; i < f.counts.x && i < uint(MAX_POINT_LIGHTS); ++i)
    {
        float3 lv = float3(L.point_lights[i].position) - wp;
        float dist = length(lv);
        if (dist < L.point_lights[i].range && dist > 1e-4)
            c += float3(L.point_lights[i].color) * L.point_lights[i].intensity * p_atten(dist, L.point_lights[i].range) * saturate(dot(n, lv / dist));
    }
    for (uint j = 0u; j < f.counts.y && j < uint(MAX_SPOT_LIGHTS); ++j)
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
            c += float3(L.spot_lights[j].color) * L.spot_lights[j].intensity * p_atten(dist, L.spot_lights[j].range) * fade * saturate(dot(n, Lj));
        }
    }
    return c;
}

static float3 aces_tonemap(float3 color, float exposure, uint mode)
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

fragment float4 DecalPS(DVOut in [[stage_in]], constant DFrame& f [[buffer(0)]], constant DBatch& b [[buffer(1)]],
                        constant DLights& L [[buffer(2)]],
                        texture2d<float> tex [[texture(0)]], sampler smp [[sampler(0)]],
                        depth2d<float> depth [[texture(1)]], sampler dsmp [[sampler(1)]])
{
    uint2 px = uint2(max(in.pos.xy, float2(0.0)));
    px = min(px, uint2(depth.get_width() - 1, depth.get_height() - 1));
    float d = depth.read(px);
    if (d >= 0.99999) discard_fragment();   // sky
    float2 ndc = float2(in.pos.x * f.screen.z * 2.0 - 1.0, 1.0 - in.pos.y * f.screen.w * 2.0);
    float4 h = f.inv_view_projection * float4(ndc, d, 1.0);
    float3 wp = h.xyz / h.w;
    float3 lp = wp.x * in.inv0.xyz + wp.y * in.inv1.xyz + wp.z * in.inv2.xyz + in.inv3.xyz;
    if (any(abs(lp) > 0.5)) discard_fragment();
    float3 n = normalize(cross(dfdy(wp), dfdx(wp)));
    if (dot(n, f.eye.xyz - wp) < 0.0) n = -n;
    float facing = dot(n, in.axis);
    float fade = in.params.x > 0.001 ? saturate(facing / in.params.x) : (facing > 0.0 ? 1.0 : 0.0);
    fade *= 1.0 - smoothstep(0.35, 0.5, abs(lp.y));
    if (in.params.y > 0.0) fade *= saturate(1.0 - length(wp - f.eye.xyz) / in.params.y);
    float2 uv = float2(lp.x + 0.5, 0.5 - lp.z);
    float4 t = b.has_texture != 0u ? tex.sample(smp, uv) : float4(1.0);
    float4 c = t * b.base_color * in.color;
    c.a *= fade;
    if (c.a <= 0.002) discard_fragment();
    float fogf = fog_amount(f, wp);
    if (b.blend == 1u) return float4(mix(float3(1.0), c.rgb, c.a * (1.0 - fogf)), 1.0);
    if (b.blend == 2u) return float4(c.rgb * c.a * (1.0 - fogf), 1.0);
    float3 lin = pow(max(c.rgb, float3(0.0)), float3(2.2)) * decal_light(f, L, wp, n);
    float3 rgb = pow(aces_tonemap(lin, f.fog2.z, f.counts.z), float3(1.0 / 2.2));
    rgb = mix(rgb, f.fog.rgb, fogf);
    return float4(rgb, c.a);
}
