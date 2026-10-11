// Procedural gradient skybox — Metal port of skybox.hlsl (sky/horizon/ground gradient + sun disc + glow).
#include <metal_stdlib>
using namespace metal;

struct SkyboxConstants
{
    float4x4 inverse_view_projection;
    packed_float3 camera_position; float padding0;
    packed_float3 sky_color;       float padding1;
    packed_float3 horizon_color;   float padding2;
    packed_float3 ground_color;    float padding3;
    packed_float3 sun_direction;   float sun_intensity;
    packed_float3 sun_color;       float tonemapper;   // the HDR path's curve: 0 = ACES fit, 1 = PBR Neutral
    float4 params;
    float4 haze;          // @176  aerial perspective on the HDR sky: rgb fog colour, w density (0 = off)
    float4 glow_dir;      // @192  xyz the direction the sun's light travels, w the fog's sun glow
    float4 glow_color;    // @208  rgb the sun's colour   // x: 1 = sample the equirect texture (#326), y: exposure, z: yaw offset (radians)
};

struct SkyOut
{
    float4 pos [[position]];
    float3 world_dir;
};

vertex SkyOut SkyVS(uint id [[vertex_id]], constant SkyboxConstants& c [[buffer(0)]])
{
    SkyOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 1.0, 1.0);
    o.pos.y = -o.pos.y;
    float4 clip = float4(uv * 2.0 - 1.0, 1.0, 1.0);
    clip.y = -clip.y;
    float4 world = c.inverse_view_projection * clip;
    o.world_dir = world.xyz / world.w - float3(c.camera_position);
    return o;
}

fragment float4 SkyPS(SkyOut in [[stage_in]], constant SkyboxConstants& c [[buffer(0)]],
                      texture2d<float> sky_tex [[texture(0)]], sampler sky_smp [[sampler(0)]])
{
    float3 dir = normalize(in.world_dir);
    float y = dir.y;

    // Equirect texture sky (#326): sampled at the far plane, no depth write, centred on the rendering camera.
    // Mip 0 — the atan2 seam would otherwise pull the smallest mip into a visible line.
    if (c.params.x > 0.5)
    {
        const float two_pi = 6.28318530718;
        float u = atan2(dir.x, dir.z) / two_pi + 0.5 + c.params.z / two_pi;
        float v = acos(clamp(y, -1.0, 1.0)) / 3.14159265359;
        float3 sky = sky_tex.sample(sky_smp, float2(u, v), level(0)).rgb * c.params.y;
        if (c.params.w > 0.0 && c.haze.w > 0.0)
        {
            // aerial perspective on the sky: the horizon whitens into the scene's haze (the path through the air grows as
            // 1 / sin(elevation)), the sun glows in it, the zenith keeps its blue
            float h = max(y, 0.0);
            float f = 1.0 - exp(-c.haze.w * 40.0 / (h + 0.04));
            float mu = saturate(dot(dir, normalize(-c.glow_dir.xyz)));
            float3 hz = c.haze.rgb + c.glow_color.rgb * (c.glow_dir.w * (0.12 * mu * mu + 0.88 * pow(mu, 10.0)));
            sky = mix(sky, hz, f);
        }
        if (c.params.w < 0.0) sky = pow(saturate(sky), 1.0 / 2.2);   // a float picture the classic way: gamma-encoded, clamped
        else if (c.params.w > 0.0)
        {
            // an HDR picture: the scene exposure + ACES + gamma of the lit surfaces, so the sky and the world agree
            float3 x = max(sky * c.params.w, 0.0);
            if (c.tonemapper > 0.5)
            {
                // Khronos PBR Neutral, as in standard.metal
                float mn = min(x.r, min(x.g, x.b));
                x -= mn < 0.08 ? mn - 6.25 * mn * mn : 0.04;
                float peak = max(x.r, max(x.g, x.b));
                if (peak >= 0.76)
                {
                    float new_peak = 1.0 - 0.0576 / (peak + 0.24 - 0.76);
                    x *= new_peak / peak;
                    float g = 1.0 - 1.0 / (0.15 * (peak - new_peak) + 1.0);
                    x = mix(x, float3(new_peak), g);
                }
                sky = pow(saturate(x), 1.0 / 2.2);
            }
            else
            {
                float3 a = x * (x + 0.0245786) - 0.000090537;
                float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
                sky = pow(max(saturate(a / b), 0.0), 1.0 / 2.2);
            }
        }
        return float4(sky, 1.0);
    }
    float3 color;
    if (y > 0.0)
    {
        float t = pow(y, 0.4);
        color = mix(float3(c.horizon_color), float3(c.sky_color), t);
    }
    else
    {
        float t = pow(-y, 0.7);
        color = mix(float3(c.horizon_color), float3(c.ground_color), t);
    }

    if (c.sun_intensity > 0.001)
    {
        float3 sun_dir = normalize(-float3(c.sun_direction));
        float sun_dot = dot(dir, sun_dir);
        float sun_disc = smoothstep(0.9995, 0.9999, sun_dot);
        color += float3(c.sun_color) * sun_disc * c.sun_intensity * 10.0;
        float sun_glow = pow(max(sun_dot, 0.0), 256.0);
        color += float3(c.sun_color) * sun_glow * c.sun_intensity * 0.5;
        float horizon_glow = pow(1.0 - abs(y), 4.0) * pow(max(sun_dot, 0.0), 2.0);
        color += float3(c.sun_color) * horizon_glow * c.sun_intensity * 0.3;
    }

    float noise = fract(sin(dot(dir.xz, float2(12.9898, 78.233))) * 43758.5453);
    color += (noise - 0.5) * 0.01;
    return float4(color, 1.0);
}
