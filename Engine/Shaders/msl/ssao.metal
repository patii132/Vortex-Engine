// SSAO for the SDL GPU backend (ssao.hlsl): half-res depth prepass -> Alchemy-style AO -> 4-tap blur.
// SsaoCB is byte-matched to the C++ SsaoCB; the result darkens only the ambient term in standard.metal.
#include <metal_stdlib>
using namespace metal;

struct SsaoCB
{
    float4x4 inv_proj;     // clip -> view (this view's projection only)
    float2 texel;          // 1 / AO target size
    float radius;          // world-space sample radius
    float intensity;
    float bias;
    float proj_scale;      // 0.5 * proj._22 * targetHeight
    float2 pad;
    // screen-space GI (#257 step 1) — appended @96
    float4x4 proj;              // view -> clip
    float4x4 view_to_prev_clip; // view -> the previous frame's clip
    float gi_strength;          // 0 = AO only
    float scene_exposure;       // the previous frame's pre-tonemap multiplier
    float prev_valid;           // 1 = prev_color is last frame's opaque picture at this size
    float gi_radius;            // world-space trace length (m)
};

struct FsOut
{
    float4 pos [[position]];
    float2 uv;
};

vertex FsOut SsaoVS(uint id [[vertex_id]])
{
    FsOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

static inline float3 view_pos(depth2d<float> depth, sampler smp, constant SsaoCB& c, float2 uv)
{
    float d = depth.sample(smp, uv);
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 v = c.inv_proj * float4(ndc, d, 1.0);
    return v.xyz / max(v.w, 1e-6);
}

static inline float hash12(float2 p)
{
    p = fract(p * float2(443.8975, 397.2973));
    p += dot(p, p.yx + 19.19);
    return fract(p.x * p.y);
}

fragment float4 SsaoPS(FsOut in [[stage_in]], constant SsaoCB& c [[buffer(0)]],
                       depth2d<float> depth [[texture(0)]], sampler smp [[sampler(0)]],
                       texture2d<float> prev_color [[texture(1)]], sampler psmp [[sampler(1)]])
{
    float d = depth.sample(smp, in.uv);
    if (d >= 0.9999) return float4(1.0);
    float3 P = view_pos(depth, smp, c, in.uv);
    float3 N = normalize(cross(dfdy(P), dfdx(P)));
    float pix_radius = clamp(c.proj_scale * c.radius / max(P.z, 0.1), 2.0, 64.0);
    const int TAPS = 12;
    float rot = hash12(in.pos.xy) * 6.2831853;
    float occlusion = 0.0;
    for (int k = 0; k < TAPS; ++k)
    {
        float a = rot + (float)k * 2.3999632;
        float r = pix_radius * sqrt(((float)k + 0.7) / (float)TAPS);
        float2 duv = float2(cos(a), sin(a)) * r * c.texel;
        float3 S = view_pos(depth, smp, c, in.uv + duv);
        float3 v = S - P;
        occlusion += max(0.0, dot(v, N) - c.bias) / (dot(v, v) + 0.01);
    }
    float ao = saturate(1.0 - c.intensity * occlusion * (2.0 / (float)TAPS));

    // ---- screen-space GI: cosine-weighted rays against the same depth, radiance from the previous frame reprojected ----
    float3 gi = float3(0.0);
    if (c.gi_strength > 0.0 && c.prev_valid > 0.5)
    {
        float3 up = abs(N.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
        float3 T = normalize(cross(up, N)), B = cross(N, T);
        float h2 = hash12(in.pos.yx + 7.31);
        const int DIRS = 6, STEPS = 6;
        for (int k = 0; k < DIRS; ++k)
        {
            float u1 = fract(rot * 0.159155 + (float)k * 0.618034), u2 = fract(h2 + (float)k * 0.381966);
            float rr = sqrt(u1), ph = 6.2831853 * u2;
            float3 dir = T * (rr * cos(ph)) + B * (rr * sin(ph)) + N * sqrt(max(0.0, 1.0 - u1));
            float3 pos = P;
            float step = c.gi_radius / (float)STEPS * (0.6 + 0.8 * u2);
            for (int s = 0; s < STEPS; ++s)
            {
                pos += dir * step * (1.0 + 0.35 * (float)s);
                float4 cl = c.proj * float4(pos, 1.0);
                if (cl.w <= 0.01) break;
                float2 uv = float2(cl.x / cl.w * 0.5 + 0.5, 0.5 - cl.y / cl.w * 0.5);
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;
                float sd = depth.sample(smp, uv);
                if (sd >= 0.9999) continue;
                float3 S = view_pos(depth, smp, c, uv);
                float dz = pos.z - S.z;
                if (dz > 0.03 && dz < step * 2.5)
                {
                    if (dot(normalize(S - P), N) < 0.2) break;   // grazing the surface itself (a slope): not a bounce
                    float4 pc = c.view_to_prev_clip * float4(S, 1.0);
                    if (pc.w > 0.01)
                    {
                        float2 puv = float2(pc.x / pc.w * 0.5 + 0.5, 0.5 - pc.y / pc.w * 0.5);
                        if (puv.x >= 0.0 && puv.x <= 1.0 && puv.y >= 0.0 && puv.y <= 1.0)
                        {
                            float3 lin = pow(max(prev_color.sample(psmp, puv).rgb, 0.0), 2.2);
                            gi += lin / max(1.0 - 0.6 * lin, 0.25) / max(c.scene_exposure, 0.05);
                        }
                    }
                    break;
                }
            }
        }
        gi *= c.gi_strength / (float)DIRS;
        float gl = dot(gi, float3(0.2126, 0.7152, 0.0722));
        gi = mix(float3(gl), gi, 0.65);   // a bounce is never as saturated as the albedo squared suggests
    }
    return float4(sqrt(saturate(gi * 0.25)), ao);
}

fragment float4 SsaoBlurPS(FsOut in [[stage_in]], constant SsaoCB& c [[buffer(0)]],
                           texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]])
{
    float4 s = float4(0.0);
    s += src.sample(smp, in.uv + c.texel * float2(-0.5, -0.5));
    s += src.sample(smp, in.uv + c.texel * float2( 1.5, -0.5));
    s += src.sample(smp, in.uv + c.texel * float2(-0.5,  1.5));
    s += src.sample(smp, in.uv + c.texel * float2( 1.5,  1.5));
    return s * 0.25;   // rgb = GI, a = AO
}
