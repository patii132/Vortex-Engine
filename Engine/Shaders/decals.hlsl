// Projected decals on DirectX 12 (#120) — the HLSL twin of decals.metal / the GLSL decals.* set. Every decal is a
// unit box drawn as its BACK faces (36 vertices from SV_VertexID, no vertex buffer; the pass culls front faces and
// tests no depth, so a camera inside the box still sees it). The pixel shader reconstructs the scene position from
// the depth copy, rejects pixels outside the box, projects along the box's local +Y and blends the material over
// the scene: lit like a surface (ACES like particles / the standard shader), multiplied in, or added.
// Conventions as in standard.hlsl: row-major matrices, mul(vec, mat). DFrame (256) / DBatch (32) / GpuDecal (160)
// are byte-matched to DX12Decals.cpp and Graphics/Decals/Decals.h; the light block is the renderer's light buffer.

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

cbuffer DFrame : register(b0)
{
    row_major float4x4 view_projection;       // @0
    row_major float4x4 inv_view_projection;   // @64
    float4 eye;                               // @128
    float4 screen;                            // @144 w, h, 1/w, 1/h
    float4 depth_params;                      // @160 x near, y far, z ortho
    float4 fog;                               // @176 rgb, density
    float4 fog2;                              // @192 height_y, height_falloff, z = scene exposure, w = fog sun glow
    float4 sun_dir;                           // @208 xyz = direction the light travels, w = intensity
    float4 sun_color;                         // @224 rgb, w = ambient strength
    uint4  counts;                            // @240 x = point lights, y = spot lights, z = tonemapper
};                                            // = 256

cbuffer DBatch : register(b1)
{
    uint   base;          // first instance of this batch
    uint   blend;         // 0 lit, 1 multiply, 2 additive
    uint   has_texture;
    uint   pad;
    float4 base_color;    // the material's base colour (× the decal tint)
};                        // = 32

struct PointLight { float3 position; float range; float3 color; float intensity; };
struct SpotLight
{
    float3 position;  float range;
    float3 direction; float spot_angle;
    float3 color;     float intensity;
    float inner_spot_angle; float shadow_strength; float shadow_bias; float shadow_slot;
};
cbuffer DLights : register(b2)
{
    PointLight point_lights[MAX_POINT_LIGHTS];
    SpotLight  spot_lights[MAX_SPOT_LIGHTS];
};

struct GpuDecal
{
    row_major float4x4 world;       // unit box -> world
    row_major float4x4 inv_world;   // world -> unit box
    float4 color;                   // tint rgb + opacity
    float4 params;                  // x angle fade, y fade distance
};                                  // 160 bytes
StructuredBuffer<GpuDecal> Decals : register(t2);

Texture2D        DecalTexture : register(t0);
Texture2D<float> SceneDepth   : register(t1);
SamplerState     LinearClamp  : register(s0);

struct DVOut
{
    float4 pos : SV_Position;
    nointerpolation float4 inv0   : TEXCOORD0;   // rows of inv_world
    nointerpolation float4 inv1   : TEXCOORD1;
    nointerpolation float4 inv2   : TEXCOORD2;
    nointerpolation float4 inv3   : TEXCOORD3;
    nointerpolation float3 axis   : TEXCOORD4;   // the box's local +Y in world space
    nointerpolation float4 color  : COLOR0;
    nointerpolation float4 params : TEXCOORD5;
};

// The unit cube as 12 triangles, clockwise from outside (front faces in D3D's default convention).
static const float3 CUBE[36] =
{
    // -Z
    float3(-0.5, -0.5, -0.5), float3(-0.5,  0.5, -0.5), float3( 0.5,  0.5, -0.5),
    float3(-0.5, -0.5, -0.5), float3( 0.5,  0.5, -0.5), float3( 0.5, -0.5, -0.5),
    // +Z
    float3(-0.5, -0.5,  0.5), float3( 0.5, -0.5,  0.5), float3( 0.5,  0.5,  0.5),
    float3(-0.5, -0.5,  0.5), float3( 0.5,  0.5,  0.5), float3(-0.5,  0.5,  0.5),
    // -X
    float3(-0.5, -0.5, -0.5), float3(-0.5, -0.5,  0.5), float3(-0.5,  0.5,  0.5),
    float3(-0.5, -0.5, -0.5), float3(-0.5,  0.5,  0.5), float3(-0.5,  0.5, -0.5),
    // +X
    float3( 0.5, -0.5, -0.5), float3( 0.5,  0.5, -0.5), float3( 0.5,  0.5,  0.5),
    float3( 0.5, -0.5, -0.5), float3( 0.5,  0.5,  0.5), float3( 0.5, -0.5,  0.5),
    // -Y
    float3(-0.5, -0.5, -0.5), float3( 0.5, -0.5, -0.5), float3( 0.5, -0.5,  0.5),
    float3(-0.5, -0.5, -0.5), float3( 0.5, -0.5,  0.5), float3(-0.5, -0.5,  0.5),
    // +Y
    float3(-0.5,  0.5, -0.5), float3(-0.5,  0.5,  0.5), float3( 0.5,  0.5,  0.5),
    float3(-0.5,  0.5, -0.5), float3( 0.5,  0.5,  0.5), float3( 0.5,  0.5, -0.5),
};

DVOut DecalVS(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    GpuDecal d = Decals[base + instanceID];
    float3 wp = mul(float4(CUBE[vertexID % 36u], 1.0), d.world).xyz;
    DVOut o;
    o.pos = mul(float4(wp, 1.0), view_projection);
    o.inv0 = d.inv_world[0]; o.inv1 = d.inv_world[1]; o.inv2 = d.inv_world[2]; o.inv3 = d.inv_world[3];
    o.axis = normalize(float3(d.world._21, d.world._22, d.world._23));
    o.color = d.color;
    o.params = d.params;
    return o;
}

// ---- shared helpers (particles.hlsl) ----
float p_atten(float dist, float range)
{
    float d = dist / range;
    float a = saturate(1.0 - d * d);
    return a * a / (dist * dist + 0.01);
}

float fog_amount(float3 wp)
{
    if (fog.w <= 0.0) return 0.0;
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

// Lambert against the scene lights with the reconstructed surface normal — no shadows, close enough to the standard
// shader for a sticker on a wall.
float3 decal_light(float3 wp, float3 n)
{
    float3 amb = lerp(float3(0.15, 0.15, 0.18), float3(0.5, 0.55, 0.7), 0.6) * sun_color.w;
    float3 L = normalize(-sun_dir.xyz);
    float3 c = amb + sun_color.rgb * sun_dir.w * saturate(dot(n, L)) * (1.0 / PI) * 2.0;
    [loop] for (uint i = 0u; i < counts.x && i < (uint)MAX_POINT_LIGHTS; ++i)
    {
        float3 lv = point_lights[i].position - wp;
        float dist = length(lv);
        if (dist < point_lights[i].range && dist > 1e-4)
            c += point_lights[i].color * point_lights[i].intensity * p_atten(dist, point_lights[i].range) * saturate(dot(n, lv / dist));
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
            c += spot_lights[j].color * spot_lights[j].intensity * p_atten(dist, spot_lights[j].range) * fade * saturate(dot(n, Lj));
        }
    }
    return c;
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

float3 world_from_depth(float2 pix, float d)
{
    float2 ndc = float2(pix.x * screen.z * 2.0 - 1.0, 1.0 - pix.y * screen.w * 2.0);
    float4 h = mul(float4(ndc, d, 1.0), inv_view_projection);
    return h.xyz / h.w;
}

float4 DecalPS(DVOut i) : SV_Target
{
    int2 px = int2(clamp(i.pos.xy, float2(0.0, 0.0), screen.xy - 1.0));
    float d = SceneDepth.Load(int3(px, 0));
    if (d >= 0.99999) discard;   // sky
    float3 wp = world_from_depth(i.pos.xy, d);
    float3 lp = wp.x * i.inv0.xyz + wp.y * i.inv1.xyz + wp.z * i.inv2.xyz + i.inv3.xyz;
    if (any(abs(lp) > 0.5)) discard;
    // the covered surface's normal from the position derivatives, facing the camera
    float3 n = normalize(cross(ddy(wp), ddx(wp)));
    if (dot(n, eye.xyz - wp) < 0.0) n = -n;
    float facing = dot(n, i.axis);
    float fade = i.params.x > 0.001 ? saturate(facing / i.params.x) : (facing > 0.0 ? 1.0 : 0.0);
    fade *= 1.0 - smoothstep(0.35, 0.5, abs(lp.y));                       // soft top / bottom of the box
    if (i.params.y > 0.0) fade *= saturate(1.0 - length(wp - eye.xyz) / i.params.y);
    float2 uv = float2(lp.x + 0.5, 0.5 - lp.z);                           // local +Z = texture up
    float4 t = has_texture != 0u ? DecalTexture.Sample(LinearClamp, uv) : float4(1.0, 1.0, 1.0, 1.0);
    float4 c = t * base_color * i.color;
    c.a *= fade;
    if (c.a <= 0.002) discard;
    float fogf = fog_amount(wp);
    if (blend == 1u) return float4(lerp(float3(1.0, 1.0, 1.0), c.rgb, c.a * (1.0 - fogf)), 1.0);   // dst * (1 - a + a * c)
    if (blend == 2u) return float4(c.rgb * c.a * (1.0 - fogf), 1.0);                                // dst + a * c
    float3 lin = pow(max(c.rgb, 0.0), 2.2) * decal_light(wp, n);
    float3 rgb = pow(aces_tonemap(lin), 1.0 / 2.2);
    rgb = lerp(rgb, fog.rgb, fogf);
    return float4(rgb, c.a);
}
