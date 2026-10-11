// Particles on DirectX 12 (#117) — the HLSL twin of particles.metal / the GLSL particles.* set: camera-facing /
// stretched / flat billboards pulled from a structured buffer (6 vertices per instance, no vertex buffer),
// ribbons (trails + beams) from a vertex buffer, and the collision depth snapshot. Conventions as in
// standard.hlsl: row-major matrices, mul(vec, mat). The constant buffers are byte-matched to PFrame (224) /
// PBatch (48) in DX12Particles.cpp; the light block (b2) is the renderer's light constant buffer, whose first
// 1024 bytes are the point + spot lights (GPUPointLight x 16, GPUSpotLight x 8).

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define MODE_RIBBON 16u
#define PI 3.14159265359

cbuffer PFrame : register(b0)
{
    row_major float4x4 view_projection;   // @0
    float4 cam_right;                     // @64
    float4 cam_up;                        // @80
    float4 cam_forward;                   // @96
    float4 eye;                           // @112
    float4 depth_params;                  // @128 x near, y far, z ortho
    float4 fog;                           // @144 rgb, density
    float4 fog2;                          // @160 height_y, height_falloff, z = scene exposure, w = fog sun glow
    float4 sun_dir;                       // @176 xyz = direction the light travels, w = intensity
    float4 sun_color;                     // @192 rgb, w = ambient strength
    uint4  counts;                        // @208 x = point lights, y = spot lights, z = tonemapper
};

cbuffer PBatch : register(b1)
{
    uint  base;          // first instance of this batch in the structured buffer
    uint  mode;          // 0 billboard, 1 stretched, 2 horizontal, 3 vertical, 16 ribbon
    uint  tiles_x;
    uint  tiles_y;
    uint  frame_blend;
    uint  lit;
    uint  blend;         // 0 alpha, 1 additive, 2 premultiplied
    uint  has_texture;
    float soft_inv;      // 1 / soft distance (0 = hard)
    float emissive;
    float pad0;
    float pad1;
};

struct PointLight { float3 position; float range; float3 color; float intensity; };
struct SpotLight
{
    float3 position;  float range;
    float3 direction; float spot_angle;
    float3 color;     float intensity;
    float inner_spot_angle; float shadow_strength; float shadow_bias; float shadow_slot;
};
cbuffer PLights : register(b2)
{
    PointLight point_lights[MAX_POINT_LIGHTS];
    SpotLight  spot_lights[MAX_SPOT_LIGHTS];
};

struct GpuParticle
{
    float3 pos;  float size;     // world centre, height in metres (width = size * aspect)
    float3 axis; float rot;      // stretched: world streak vector (length included); rot: radians
    uint   color; float frame; float aspect; uint flags;
};                               // 48 bytes
StructuredBuffer<GpuParticle> Particles : register(t2);

Texture2D        ParticleTexture : register(t0);
Texture2D<float> SceneDepth      : register(t1);
SamplerState     LinearWrap      : register(s0);
SamplerState     PointClamp      : register(s1);

struct PVOut
{
    float4 pos     : SV_Position;
    float2 uv0     : TEXCOORD0;
    float2 uv1     : TEXCOORD1;
    float2 local   : TEXCOORD2;   // quad-local [-1,1] (procedural disc) / ribbon: y = across
    float  blend_t : TEXCOORD3;
    float  view_z  : TEXCOORD4;
    float  fogf    : TEXCOORD5;
    float4 color   : COLOR0;
    float3 light   : TEXCOORD6;
};

// ---- shared helpers (particles_common.glsl) ----
float p_atten(float dist, float range)
{
    float d = dist / range;
    float a = saturate(1.0 - d * d);
    return a * a / (dist * dist + 0.01);
}

float fog_amount(float3 wp)
{
    if (fog.w <= 0.0) return 0.0;
    // the standard shader's height fog (#328): uniform up to fog2.x, exp(-fog2.y * height) above, integrated along the ray
    float3 cam = eye.xyz;
    float dist = length(wp - cam);
    float density = fog.w, height_y = fog2.x, k = fog2.y;
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

// Volumetric-ish lighting at the particle centre: hemisphere ambient + wrapped sun + point / spot lights without N·L.
float3 particle_light(float3 wp)
{
    float3 amb = lerp(float3(0.15, 0.15, 0.18), float3(0.5, 0.55, 0.7), 0.6) * sun_color.w;
    float3 N = -cam_forward.xyz;
    float3 Ld = normalize(-sun_dir.xyz);
    float wrap = saturate(dot(N, Ld) * 0.5 + 0.5);
    float3 c = amb + sun_color.rgb * sun_dir.w * wrap * (1.0 / PI) * 2.0;
    [loop] for (uint i = 0u; i < counts.x && i < (uint)MAX_POINT_LIGHTS; ++i)
    {
        float3 lv = point_lights[i].position - wp;
        float dist = length(lv);
        if (dist < point_lights[i].range)
            c += point_lights[i].color * point_lights[i].intensity * p_atten(dist, point_lights[i].range);
    }
    [loop] for (uint j = 0u; j < counts.y && j < (uint)MAX_SPOT_LIGHTS; ++j)
    {
        float3 lv = spot_lights[j].position - wp;
        float dist = length(lv);
        if (dist < spot_lights[j].range && dist > 1e-4)
        {
            float3 Lj = lv / dist;
            float theta = dot(-Lj, normalize(spot_lights[j].direction));
            float oc = cos(spot_lights[j].spot_angle * 0.5 * PI / 180.0);
            float ic = cos(spot_lights[j].inner_spot_angle * 0.5 * PI / 180.0);
            float fade = saturate((theta - oc) / (ic - oc + 0.001));
            c += spot_lights[j].color * spot_lights[j].intensity * p_atten(dist, spot_lights[j].range) * fade;
        }
    }
    return c;
}

float2 tile_uv(float2 uv, float frame_, uint tx, uint ty)
{
    uint frames = tx * ty;
    uint fr = (uint)frame_ % max(frames, 1u);
    float2 cell = float2((float)(fr % tx), (float)(fr / tx));
    return (uv + cell) / float2((float)tx, (float)ty);
}

float4 unpack_color(uint c)
{
    return float4((float)(c & 255u), (float)((c >> 8) & 255u), (float)((c >> 16) & 255u), (float)(c >> 24)) * (1.0 / 255.0);
}

float3 aces_tonemap(float3 color)
{
    float3 x = color * (fog2.z > 0.0 ? fog2.z : 0.5);   // the scene exposure the surfaces use
    if (counts.z == 1)
    {
        // Khronos PBR Neutral (the scene's tonemapper 1): hue-preserving — see standard.hlsl
        const float startCompression = 0.8 - 0.04;
        const float desaturation = 0.15;
        float mn = min(x.r, min(x.g, x.b));
        float offset = mn < 0.08 ? mn - 6.25 * mn * mn : 0.04;
        x -= offset;
        float peak = max(x.r, max(x.g, x.b));
        if (peak < startCompression) return saturate(x);
        const float d = 1.0 - startCompression;
        float newPeak = 1.0 - d * d / (peak + d - startCompression);
        x *= newPeak / peak;
        float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
        return saturate(lerp(x, newPeak * float3(1.0, 1.0, 1.0), g));
    }
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    return saturate(a / b);
}

// ---- billboards: 6 vertices per instance from the structured buffer ----
PVOut ParticleVS(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    const float2 corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };
    GpuParticle p = Particles[base + instanceID];
    float2 c = corners[vertexID % 6u];
    float3 center = p.pos;
    float half_h = p.size * 0.5;
    float half_w = half_h * p.aspect;
    float s = sin(p.rot), co = cos(p.rot);
    float2 rc = float2(c.x * co - c.y * s, c.x * s + c.y * co);
    float3 wp;
    if (mode == 1u)
    {
        // stretched along the velocity: the streak vector is in axis, width = size
        float3 axis = p.axis;
        float3 dir = normalize(axis + float3(0, 1e-6, 0));
        float3 tocam = depth_params.z > 0.5 ? -cam_forward.xyz : (eye.xyz - center);
        float3 side = cross(dir, tocam);
        float sl = length(side);
        side = sl > 1e-6 ? side / sl : cam_right.xyz;
        wp = center + side * (c.x * half_w) + axis * (c.y * 0.5);
    }
    else if (mode == 2u)
    {
        // horizontal (lies on the XZ plane: ground ripples, decal-like splats)
        wp = center + float3(rc.x * half_w, 0.0, rc.y * half_h);
    }
    else if (mode == 3u)
    {
        // vertical (Y-axis aligned, faces the camera around Y: fire sheets)
        float3 tocam = eye.xyz - center; tocam.y = 0.0;
        float tl = length(tocam);
        float3 fwd = tl > 1e-5 ? tocam / tl : float3(0, 0, -1);
        float3 right = normalize(cross(float3(0, 1, 0), fwd));
        wp = center + right * (rc.x * half_w) + float3(0, 1, 0) * (rc.y * half_h);
    }
    else
    {
        wp = center + cam_right.xyz * (rc.x * half_w) + cam_up.xyz * (rc.y * half_h);
    }
    PVOut o;
    o.pos = mul(float4(wp, 1.0), view_projection);
    float2 uv = float2(c.x * 0.5 + 0.5, 0.5 - c.y * 0.5);
    o.uv0 = tile_uv(uv, p.frame, tiles_x, tiles_y);
    o.uv1 = tile_uv(uv, p.frame + 1.0, tiles_x, tiles_y);
    o.blend_t = frac(p.frame);
    o.local = c;
    o.view_z = dot(wp - eye.xyz, cam_forward.xyz);
    o.color = unpack_color(p.color);
    o.fogf = fog_amount(center);
    o.light = lit != 0u ? particle_light(center) : float3(1, 1, 1);
    return o;
}

// ---- ribbons: trails and beams from a vertex buffer (pos float3 @0, uv float2 @12, color ubyte4norm @20) ----
struct RibbonIn
{
    float3 pos   : POSITION;
    float2 uv    : TEXCOORD0;
    float4 color : COLOR0;
};

PVOut RibbonVS(RibbonIn v)
{
    PVOut o;
    o.pos = mul(float4(v.pos, 1.0), view_projection);
    o.uv0 = v.uv;
    o.uv1 = v.uv;
    o.blend_t = 0.0;
    o.local = float2(0.0, v.uv.y * 2.0 - 1.0);
    o.view_z = dot(v.pos - eye.xyz, cam_forward.xyz);
    o.color = v.color;
    o.fogf = fog_amount(v.pos);
    o.light = lit != 0u ? particle_light(v.pos) : float3(1, 1, 1);
    return o;
}

// ---- shading: the scene depth (a copy of this pass's depth buffer) gives the soft-particle fade ----
float linear_depth(float d)
{
    float n = depth_params.x, fa = depth_params.y;
    if (depth_params.z > 0.5) return n + d * (fa - n);
    return (n * fa) / max(fa - d * (fa - n), 1e-6);
}

float4 ParticlePS(PVOut i) : SV_Target
{
    float4 t;
    if (has_texture != 0u)
    {
        t = ParticleTexture.Sample(LinearWrap, i.uv0);
        if (frame_blend != 0u) t = lerp(t, ParticleTexture.Sample(LinearWrap, i.uv1), i.blend_t);
    }
    else
    {
        // no texture: a soft round puff (billboards) / a soft-edged strip (ribbons)
        float r = mode == MODE_RIBBON ? abs(i.local.y) : length(i.local);
        float a = saturate(1.0 - r);
        a = a * a * (3.0 - 2.0 * a);
        t = float4(1.0, 1.0, 1.0, a);
    }
    float4 c = t * i.color;
    if (blend == 2u) c.rgb *= i.color.a;   // premultiplied: the tint's alpha scales the colour too
    // soft particles against the scene depth copy (the bound depth buffer did the hard test already)
    uint w, h;
    SceneDepth.GetDimensions(w, h);
    int2 px = int2(clamp(i.pos.xy, float2(0, 0), float2((float)w - 1, (float)h - 1)));
    float scene_z = linear_depth(SceneDepth.Load(int3(px, 0)));
    float dz = scene_z - i.view_z;
    if (dz < 0.0) discard;
    float fade = soft_inv > 0.0 ? saturate(dz * soft_inv) : 1.0;
    fade *= saturate((i.view_z - depth_params.x) * 5.0);   // no pop at the near plane
    if (lit != 0u)
    {
        float3 lin = pow(max(c.rgb, 0.0), 2.2) * i.light;
        c.rgb = pow(aces_tonemap(lin), 1.0 / 2.2);
    }
    c.rgb *= emissive;
    if (blend == 1u) c.rgb *= (1.0 - i.fogf);                          // additive glows vanish into the fog
    else if (blend == 2u) c.rgb = lerp(c.rgb, fog.rgb * c.a, i.fogf);
    else c.rgb = lerp(c.rgb, fog.rgb, i.fogf);
    c.a *= fade;
    if (blend == 2u) c.rgb *= fade;
    if (c.a <= 0.001 && blend != 2u) discard;
    return c;
}

// ---- collision snapshot: scene depth -> linear view depth in metres, point-downsampled ----
cbuffer SnapCB : register(b3)
{
    float4 snap_depth_params;   // x near, y far, z ortho
    float2 snap_src_size;
    float2 snap_dst_size;
};

struct SnapOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

SnapOut SnapVS(uint vertexID : SV_VertexID)
{
    SnapOut o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

float4 SnapPS(SnapOut i) : SV_Target
{
    float2 p = clamp(floor(i.uv * snap_src_size), float2(0, 0), snap_src_size - 1.0);
    float d = SceneDepth.Load(int3((int2)p, 0));
    float n = snap_depth_params.x, fa = snap_depth_params.y;
    float z = snap_depth_params.z > 0.5 ? n + d * (fa - n) : (n * fa) / max(fa - d * (fa - n), 1e-6);
    return float4(z, 0.0, 0.0, 1.0);
}
