// SSAO (#32) — screen-space ambient occlusion from the dedicated half-res AO depth prepass.
// Two entry points share this file and the SSAO root signature (t0 + 16 root constants @ b0):
//   PSMain  Alchemy/McGuire-style AO: reconstruct the view-space position from depth, derive the
//           normal from position derivatives, take a per-pixel-rotated spiral of taps and
//           accumulate the angle-weighted, distance-falloff occlusion.
//   PSBlur  4-tap box blur (the spiral's rotation noise averages out; cheap and cache-friendly).
// The result darkens ONLY the ambient/indirect term in standard.hlsl (t10) — direct light, fog
// and emissive stay untouched.

cbuffer SsaoCB : register(b0)
{
    row_major float4x4 InvProj;   // clip -> view (this view's projection only, no view matrix)
    float2 TexelSize;             // 1 / AO-target size (the half-res target being written)
    float  Radius;                // world-space sample radius
    float  Intensity;             // occlusion strength multiplier
    float  Bias;                  // self-occlusion guard (view-space units)
    float  ProjScale;             // 0.5 * proj._22 * targetHeight — world radius -> pixels at z=1
    float2 _padS;
    // screen-space GI (#257 step 1) — appended @96
    row_major float4x4 Proj;          // view -> clip (this view's projection)
    row_major float4x4 ViewToPrevClip; // view -> the previous frame's clip (reprojection into PrevColor)
    float  GiStrength;            // 0 = AO only
    float  SceneExposure;         // the pre-tonemap multiplier the previous frame was rendered with
    float  PrevValid;             // 1 = PrevColor holds last frame's opaque picture at this view's size
    float  GiRadius;              // world-space trace length (m)
    // the bounce from the sun's shadow map (#257 step 2, a reflective shadow map read from depth alone) — appended @240
    row_major float4x4 ViewToRsm;     // view -> the bounce cascade's clip
    row_major float4x4 RsmToView;     // the bounce cascade's clip -> view
    float4 RsmTile;               // xy the cascade's tile offset in the atlas (uv), z the tile scale, w one atlas texel (uv)
    float4 RsmSunView;            // xyz towards the sun in view space, w the gather radius in tile uv
    float4 RsmFlux;               // rgb ground albedo x sun colour x intensity (the emitters' radiance before the cosines), w the gather radius (m); 0 = off
};

Texture2D    Src : register(t0);   // PSMain: AO depth (R32_FLOAT). PSBlur: raw AO + GI (RGBA8).
Texture2D    PrevColor : register(t1);   // PSMain: the previous frame's opaque colour (LDR, tonemapped)
Texture2D    Csm : register(t2);         // PSMain: the sun's cascade atlas (R32_FLOAT depth) — the bounce's emitters
SamplerState Smp : register(s0);   // point/clamp — depth must not be filtered

struct VS_OUT
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VS_OUT VSMain(uint vertexID : SV_VertexID)
{
    VS_OUT o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

// Reconstruct the view-space position of a pixel from its depth-buffer value.
float3 ViewPos(float2 uv)
{
    float d = Src.SampleLevel(Smp, uv, 0).r;
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 v = mul(float4(ndc, d, 1.0), InvProj);
    return v.xyz / max(v.w, 1e-6);
}

float Hash12(float2 p)
{
    p = frac(p * float2(443.8975, 397.2973));
    p += dot(p, p.yx + 19.19);
    return frac(p.x * p.y);
}

float4 PSMain(VS_OUT i) : SV_TARGET
{
    float d = Src.SampleLevel(Smp, i.uv, 0).r;
    if (d >= 0.9999) return 1.0;   // sky / no geometry -> unoccluded

    float3 P = ViewPos(i.uv);
    // Normal from position derivatives — no G-buffer needed. Faceted on curved surfaces at
    // half res, but the blur + ambient-only application hide it completely.
    float3 N = normalize(cross(ddy(P), ddx(P)));

    // Screen-space spiral radius for the world-space Radius at this depth.
    float pixRadius = ProjScale * Radius / max(P.z, 0.1);
    pixRadius = clamp(pixRadius, 2.0, 64.0);

    const int TAPS = 12;
    float rot = Hash12(i.pos.xy) * 6.2831853;
    float occlusion = 0.0;
    [unroll]
    for (int k = 0; k < TAPS; ++k)
    {
        // Golden-angle spiral: well-distributed taps, one rotation hash per pixel.
        float a = rot + (float)k * 2.3999632;
        float r = pixRadius * sqrt(((float)k + 0.7) / (float)TAPS);
        float2 duv = float2(cos(a), sin(a)) * r * TexelSize;
        float3 S = ViewPos(i.uv + duv);
        float3 v = S - P;
        // Angle-weighted (only geometry in the normal hemisphere occludes), distance-falloff
        // (far-away samples contribute nothing — kills halos across depth discontinuities).
        occlusion += max(0.0, dot(v, N) - Bias) / (dot(v, v) + 0.01);
    }
    float ao = saturate(1.0 - Intensity * occlusion * (2.0 / (float)TAPS));

    // ---- screen-space GI: a few cosine-weighted rays from P against the same depth; a hit's radiance comes from the
    // previous frame's picture, reprojected (the LDR output decoded back to pre-tonemap radiance) ----
    float3 gi = float3(0.0, 0.0, 0.0);
    if (GiStrength > 0.0 && PrevValid > 0.5)
    {
        float3 up = abs(N.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
        float3 T = normalize(cross(up, N)), B = cross(N, T);
        float h2 = Hash12(i.pos.yx + 7.31);
        const int DIRS = 6, STEPS = 6;
        float hits = 0.0;
        [unroll]
        for (int k = 0; k < DIRS; ++k)
        {
            float u1 = frac(rot * 0.159155 + (float)k * 0.618034), u2 = frac(h2 + (float)k * 0.381966);
            float rr = sqrt(u1), ph = 6.2831853 * u2;
            float3 dir = T * (rr * cos(ph)) + B * (rr * sin(ph)) + N * sqrt(max(0.0, 1.0 - u1));
            float3 pos = P;
            float step = GiRadius / (float)STEPS * (0.6 + 0.8 * u2);
            [unroll]
            for (int s = 0; s < STEPS; ++s)
            {
                pos += dir * step * (1.0 + 0.35 * (float)s);
                float4 c = mul(float4(pos, 1.0), Proj);
                if (c.w <= 0.01) break;
                float2 uv = float2(c.x / c.w * 0.5 + 0.5, 0.5 - c.y / c.w * 0.5);
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;
                float sd = Src.SampleLevel(Smp, uv, 0).r;
                if (sd >= 0.9999) continue;
                float3 S = ViewPos(uv);
                float dz = pos.z - S.z;
                if (dz > 0.03 && dz < step * 2.5)
                {
                    if (dot(normalize(S - P), N) < 0.2) break;   // grazing the surface itself (a slope): not a bounce
                    float4 pc = mul(float4(S, 1.0), ViewToPrevClip);
                    if (pc.w > 0.01)
                    {
                        float2 puv = float2(pc.x / pc.w * 0.5 + 0.5, 0.5 - pc.y / pc.w * 0.5);
                        if (puv.x >= 0.0 && puv.x <= 1.0 && puv.y >= 0.0 && puv.y <= 1.0)
                        {
                            float3 lin = pow(max(PrevColor.SampleLevel(Smp, puv, 0).rgb, 0.0), 2.2);
                            float3 rad = lin / max(1.0 - 0.6 * lin, 0.25) / max(SceneExposure, 0.05);
                            gi += rad; hits += 1.0;
                        }
                    }
                    break;
                }
            }
        }
        gi *= GiStrength / (float)DIRS;
        float gl = dot(gi, float3(0.2126, 0.7152, 0.0722));
        gi = lerp(float3(gl, gl, gl), gi, 0.65);   // a bounce is never as saturated as the albedo squared suggests
    }
    // ---- the bounce from the sun's shadow map (#257 step 2): every point the sun sees is a Lambertian emitter of the ground's
    // colour; a disk of them around the receiver in the cascade is gathered with the full geometric term, their normals from
    // the map's own depth gradient. Off-screen sand lights the shaded floor and the trunks — what the screen trace cannot see.
    if (GiStrength > 0.0 && RsmFlux.w > 0.0)
    {
        float4 lp = mul(float4(P, 1.0), ViewToRsm);
        if (lp.w > 0.001)
        {
            float2 tuv = (lp.xy / lp.w) * float2(0.5, -0.5) + 0.5;
            if (tuv.x > 0.02 && tuv.x < 0.98 && tuv.y > 0.02 && tuv.y < 0.98)
            {
                float3 L = RsmSunView.xyz;
                float R = RsmSunView.w, Rm = RsmFlux.w;
                const int K = 12;
                float area = 3.14159265 * Rm * Rm / (float)K;
                float tx = RsmTile.w / RsmTile.z;   // one atlas texel in tile uv
                float3 E = float3(0.0, 0.0, 0.0);
                float ang = Hash12(i.pos.xy * 1.7 + 3.1) * 6.2831853;
                [unroll]
                for (int k = 0; k < K; ++k)
                {
                    float a = ang + (float)k * 2.3999632;
                    float r = R * sqrt(((float)k + 0.5) / (float)K);
                    float2 uvk = tuv + float2(cos(a), sin(a)) * r;
                    float2 auv = uvk * RsmTile.z + RsmTile.xy;
                    float dk = Csm.SampleLevel(Smp, auv, 0).r;
                    if (dk >= 0.9999) continue;
                    float2 nk = uvk * float2(2.0, -2.0) + float2(-1.0, 1.0);
                    float4 vx = mul(float4(nk, dk, 1.0), RsmToView);
                    float3 X = vx.xyz / vx.w;
                    float dkx = Csm.SampleLevel(Smp, auv + float2(RsmTile.w, 0.0), 0).r;
                    float dky = Csm.SampleLevel(Smp, auv + float2(0.0, RsmTile.w), 0).r;
                    float4 vxx = mul(float4(nk + float2(2.0 * tx, 0.0), dkx, 1.0), RsmToView);
                    float4 vxy = mul(float4(nk - float2(0.0, 2.0 * tx), dky, 1.0), RsmToView);
                    float3 Nk = cross(vxx.xyz / vxx.w - X, vxy.xyz / vxy.w - X);
                    float nl = dot(Nk, Nk);
                    if (nl < 1e-12) continue;
                    Nk *= rsqrt(nl);
                    if (dot(Nk, L) < 0.0) Nk = -Nk;   // the sun sees it, so it faces the sun
                    float3 w = X - P;
                    float d2 = max(dot(w, w), 0.25);
                    w *= rsqrt(d2);
                    float g = max(dot(N, w), 0.0) * max(dot(Nk, -w), 0.0) * max(dot(Nk, L), 0.0);
                    E += g * (area / d2);
                }
                gi += RsmFlux.rgb * E * (GiStrength / 3.14159265);
            }
        }
    }
    return float4(sqrt(saturate(gi * 0.25)), ao);
}

float4 PSBlur(VS_OUT i) : SV_TARGET
{
    float4 s = float4(0.0, 0.0, 0.0, 0.0);
    s += Src.SampleLevel(Smp, i.uv + TexelSize * float2(-0.5, -0.5), 0);
    s += Src.SampleLevel(Smp, i.uv + TexelSize * float2( 1.5, -0.5), 0);
    s += Src.SampleLevel(Smp, i.uv + TexelSize * float2(-0.5,  1.5), 0);
    s += Src.SampleLevel(Smp, i.uv + TexelSize * float2( 1.5,  1.5), 0);
    return s * 0.25;   // rgb = GI, a = AO
}
