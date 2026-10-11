// Post-processing uber-pass (#28 framework / #29 pack 1) — vignette + film grain + chromatic
// aberration in ONE fullscreen pass (feature bits in Flags avoid three RT round-trips), plus a
// trivial invert used only to verify the chain's ping-pong plumbing. Runs at output resolution
// between the upscale/DLSS composite and the UI overlay; reads color only, writes no depth.
//
// The PostFx cbuffer is byte-matched to DX12PostFxChain::PassCB — keep both in sync.

cbuffer PostFx : register(b0)
{
    float2 TexelSize;      // 1 / output size
    float  Time;           // seconds since renderer start (grain re-seed)
    uint   Flags;          // 1 vignette, 2 grain, 4 chromatic aberration, 8 invert, 16 color grading, 32 bloom, 64 FXAA
    float4 Vignette;       // x intensity, y smoothness, z roundness (1 = circular), w unused
    float4 VignetteColor;  // rgb linear, w unused
    float4 GrainCA;        // x grain intensity, y grain size (px), z CA strength, w CA radial falloff
    float4 Grade1;         // x exposure (stops), y contrast, z saturation, w temperature (-1..1)
    float4 Grade2;         // x tint (-1..1 green<->magenta), yzw reserved
    float4 BloomP;         // x bloom composite intensity, yzw reserved
};

Texture2D    Src      : register(t0);
Texture2D    BloomTex : register(t1);   // bloom.hlsl chain result (mip 0) — only sampled under flag 32
SamplerState Smp      : register(s0);

struct VS_OUT
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

// Fullscreen triangle from SV_VertexID — no vertex buffer, no input layout (same trick as upscale.hlsl).
VS_OUT VSMain(uint vertexID : SV_VertexID)
{
    VS_OUT o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

// Cheap 2D hash (no noise texture): stable per grain cell, re-seeded per frame via Time.
float Hash21(float2 p)
{
    p = frac(p * float2(443.8975, 397.2973));
    p += dot(p, p.yx + 19.19);
    return frac(p.x * p.y);
}

// FXAA 3.11 (the console variant): the luma of the 3x3 neighbourhood picks the edge direction, two taps along it blend the
// edge, two wider taps are kept only while they stay inside the local luma range — nine samples, no history.
float FxaaLuma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float3 Fxaa(float2 uv)
{
    float3 rgbM = Src.Sample(Smp, uv).rgb;
    float3 rgbNW = Src.Sample(Smp, uv + float2(-1.0, -1.0) * TexelSize).rgb;
    float3 rgbNE = Src.Sample(Smp, uv + float2(1.0, -1.0) * TexelSize).rgb;
    float3 rgbSW = Src.Sample(Smp, uv + float2(-1.0, 1.0) * TexelSize).rgb;
    float3 rgbSE = Src.Sample(Smp, uv + float2(1.0, 1.0) * TexelSize).rgb;
    float lumaM = FxaaLuma(rgbM), lumaNW = FxaaLuma(rgbNW), lumaNE = FxaaLuma(rgbNE), lumaSW = FxaaLuma(rgbSW), lumaSE = FxaaLuma(rgbSE);
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
    float2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y = ((lumaNW + lumaSW) - (lumaNE + lumaSE));
    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * (1.0 / 8.0)), 1.0 / 128.0);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = min(float2(8.0, 8.0), max(float2(-8.0, -8.0), dir * rcpDirMin)) * TexelSize;
    float3 rgbA = 0.5 * (Src.Sample(Smp, uv + dir * (1.0 / 3.0 - 0.5)).rgb + Src.Sample(Smp, uv + dir * (2.0 / 3.0 - 0.5)).rgb);
    float3 rgbB = rgbA * 0.5 + 0.25 * (Src.Sample(Smp, uv + dir * -0.5).rgb + Src.Sample(Smp, uv + dir * 0.5).rgb);
    float lumaB = FxaaLuma(rgbB);
    return (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
}

float4 PSMain(VS_OUT i) : SV_TARGET
{
    float2 uv = i.uv;
    float3 col;

    if (Flags & 4)
    {
        // Chromatic aberration: radial RGB split growing towards the screen edges. Strength is in
        // "percent of half-screen" units (0.35 = subtle horror unease, 2+ = heavy VHS smear).
        float2 fromC = uv - 0.5;
        float  r = saturate(length(fromC) * 2.0);
        float  amt = GrainCA.z * 0.01 * pow(r, max(GrainCA.w, 0.01));
        col.r = Src.Sample(Smp, uv + fromC * amt).r;
        col.g = Src.Sample(Smp, uv).g;
        col.b = Src.Sample(Smp, uv - fromC * amt).b;
    }
    else if (Flags & 64)
    {
        col = Fxaa(uv);   // anti-aliasing on the LDR picture (before bloom / grain / grading)
    }
    else
    {
        col = Src.Sample(Smp, uv).rgb;
    }

    if (Flags & 32)
    {
        // Bloom (#30): additively composite the blurred bright-pass chain (bloom.hlsl, sampled
        // bilinearly from its half-res mip 0). Added BEFORE grain/grading/vignette so the glow is
        // shaped by the grade and darkened by the vignette like any other scene light.
        col += BloomTex.Sample(Smp, uv).rgb * BloomP.x;
    }

    if (Flags & 2)
    {
        // Film grain: hash noise per size-px cell, luminance-weighted so shadows grain more than
        // highlights (film-negative behaviour — and horror lives in the shadows).
        float2 cell = floor(i.pos.xy / max(GrainCA.y, 1.0));
        float  n = Hash21(cell + frac(Time * float2(17.131, 3.7171)) * 289.17) * 2.0 - 1.0;
        float  luma = dot(col, float3(0.299, 0.587, 0.114));
        col = saturate(col + n * GrainCA.x * 0.25 * (1.0 - saturate(luma)));
    }

    if (Flags & 16)
    {
        // Color grading (#31): exposure -> white balance -> contrast -> saturation. Order matters (photographic).
        col *= exp2(Grade1.x);                                   // exposure in stops (2^EV)

        // White balance: warm/cool (temperature) on the R/B axis, green/magenta (tint) on the G axis.
        // Cheap channel scale around 1 — enough for a mood shift without a full chromatic-adaptation matrix.
        float3 wb = float3(1.0 + Grade1.w * 0.2, 1.0 + Grade2.x * 0.2, 1.0 - Grade1.w * 0.2);
        col *= wb;

        col = (col - 0.5) * max(Grade1.y, 0.0) + 0.5;            // contrast around mid-grey
        float luma = dot(col, float3(0.299, 0.587, 0.114));      // saturation: lerp from luminance
        col = lerp(luma.xxx, col, Grade1.z);
        col = max(col, 0.0);
    }

    if (Flags & 1)
    {
        // Vignette: intensity scales the center distance, smoothness the falloff curve, roundness 1
        // aspect-corrects to a true circle (0 hugs the screen shape). Darkens towards VignetteColor.
        float  aspect = TexelSize.y / TexelSize.x;   // = width / height
        float2 d = (uv - 0.5) * 2.0 * Vignette.x;
        d.x *= lerp(1.0, aspect, Vignette.z);
        float  vig = pow(saturate(1.0 - dot(d, d)), Vignette.y * 4.0 + 0.001);
        col = lerp(VignetteColor.rgb, col, vig);
    }

    if (Flags & 8)
        col = 1.0 - col;   // chain-verification pass only — never enabled in production

    return float4(col, 1.0);
}
