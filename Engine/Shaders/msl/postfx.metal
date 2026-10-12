// Fullscreen passes for the SDL GPU backend:
//   BlitVS/BlitPS   — render-scale composite (upscale.hlsl): samples the scene RT onto the present target
//   PostFxPS        — post-processing uber pass (postfx.hlsl): chromatic aberration, bloom composite, grain,
//                     color grading, vignette, debug invert. PostFxCB is byte-matched to the C++ PassCB.
#include <metal_stdlib>
using namespace metal;

struct BlitOut
{
    float4 pos [[position]];
    float2 uv;
};

vertex BlitOut BlitVS(uint id [[vertex_id]])
{
    BlitOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

fragment float4 BlitPS(BlitOut in [[stage_in]], texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]])
{
    return src.sample(smp, in.uv);
}

struct PostFxCB
{
    float2 texel_size;
    float  time;
    uint   flags;            // 1 vignette, 2 grain, 4 chromatic aberration, 8 invert, 16 color grading, 32 bloom
    float4 vignette;         // intensity, smoothness, roundness, unused
    float4 vignette_color;   // rgb, unused
    float4 grain_ca;         // grain intensity, grain size, ca strength, ca falloff
    float4 grade1;           // exposure, contrast, saturation, temperature
    float4 grade2;           // tint, reserved...
    float4 bloom;            // composite intensity, reserved...
};

static inline float hash21(float2 p)
{
    p = fract(p * float2(443.8975, 397.2973));
    p += dot(p, p.yx + 19.19);
    return fract(p.x * p.y);
}

// FXAA 3.11 (the console variant): nine samples, no history — see postfx.hlsl
static inline float fxaa_luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
static inline float3 fxaa(texture2d<float> src, sampler smp, float2 uv, float2 texel)
{
    float3 rgbM = src.sample(smp, uv).rgb;
    float3 rgbNW = src.sample(smp, uv + float2(-1.0, -1.0) * texel).rgb;
    float3 rgbNE = src.sample(smp, uv + float2(1.0, -1.0) * texel).rgb;
    float3 rgbSW = src.sample(smp, uv + float2(-1.0, 1.0) * texel).rgb;
    float3 rgbSE = src.sample(smp, uv + float2(1.0, 1.0) * texel).rgb;
    float lumaM = fxaa_luma(rgbM), lumaNW = fxaa_luma(rgbNW), lumaNE = fxaa_luma(rgbNE), lumaSW = fxaa_luma(rgbSW), lumaSE = fxaa_luma(rgbSE);
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
    float2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y = ((lumaNW + lumaSW) - (lumaNE + lumaSE));
    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * (1.0 / 8.0)), 1.0 / 128.0);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = min(float2(8.0), max(float2(-8.0), dir * rcpDirMin)) * texel;
    float3 rgbA = 0.5 * (src.sample(smp, uv + dir * (1.0 / 3.0 - 0.5)).rgb + src.sample(smp, uv + dir * (2.0 / 3.0 - 0.5)).rgb);
    float3 rgbB = rgbA * 0.5 + 0.25 * (src.sample(smp, uv + dir * -0.5).rgb + src.sample(smp, uv + dir * 0.5).rgb);
    float lumaB = fxaa_luma(rgbB);
    return (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
}

fragment float4 PostFxPS(BlitOut in [[stage_in]], constant PostFxCB& c [[buffer(0)]],
                         texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]],
                         texture2d<float> bloom_tex [[texture(1)]], sampler bloom_smp [[sampler(1)]])
{
    float2 uv = in.uv;
    float3 col;
    if (c.flags & 4u)
    {
        float2 from_c = uv - 0.5;
        float r = saturate(length(from_c) * 2.0);
        float amt = c.grain_ca.z * 0.01 * pow(r, max(c.grain_ca.w, 0.01));
        col.r = src.sample(smp, uv + from_c * amt).r;
        col.g = src.sample(smp, uv).g;
        col.b = src.sample(smp, uv - from_c * amt).b;
    }
    else if (c.flags & 64u)
    {
        col = fxaa(src, smp, uv, c.texel_size);   // anti-aliasing on the LDR picture
    }
    else
    {
        col = src.sample(smp, uv).rgb;
    }

    if (c.flags & 32u)
        col += bloom_tex.sample(bloom_smp, uv).rgb * c.bloom.x;

    if (c.flags & 2u)
    {
        float2 cell = floor(in.pos.xy / max(c.grain_ca.y, 1.0));
        float n = hash21(cell + fract(c.time * float2(17.131, 3.7171)) * 289.17) * 2.0 - 1.0;
        float luma = dot(col, float3(0.299, 0.587, 0.114));
        col = saturate(col + n * c.grain_ca.x * 0.25 * (1.0 - saturate(luma)));
    }

    if (c.flags & 16u)
    {
        col *= exp2(c.grade1.x);
        float3 wb = float3(1.0 + c.grade1.w * 0.2, 1.0 + c.grade2.x * 0.2, 1.0 - c.grade1.w * 0.2);
        col *= wb;
        col = (col - 0.5) * max(c.grade1.y, 0.0) + 0.5;
        float luma = dot(col, float3(0.299, 0.587, 0.114));
        col = mix(float3(luma), col, c.grade1.z);
        col = max(col, 0.0);
    }

    if (c.flags & 1u)
    {
        float aspect = c.texel_size.y / c.texel_size.x;
        float2 d = (uv - 0.5) * 2.0 * c.vignette.x;
        d.x *= mix(1.0, aspect, c.vignette.z);
        float vig = pow(saturate(1.0 - dot(d, d)), c.vignette.y * 4.0 + 0.001);
        col = mix(c.vignette_color.rgb, col, vig);
    }

    if (c.flags & 8u)
        col = 1.0 - col;

    return float4(col, 1.0);
}
