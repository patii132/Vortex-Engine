#version 450
#extension GL_GOOGLE_include_directive : require
// Post-processing uber pass (postfx.metal PostFxPS): chromatic aberration, bloom composite, grain,
// colour grading, vignette, debug invert. PostFxCB is byte-matched to the C++ PassCB.
#include "common.glsl"

layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_src;
layout(set = 2, binding = 1) uniform sampler2D u_bloom;

layout(set = 3, binding = 0, std140) uniform PostFxCB
{
	vec2  texel_size;
	float time;
	uint  flags;            // 1 vignette, 2 grain, 4 chromatic aberration, 8 invert, 16 colour grading, 32 bloom
	vec4  vignette;         // intensity, smoothness, roundness, unused
	vec4  vignette_color;   // rgb, unused
	vec4  grain_ca;         // grain intensity, grain size, ca strength, ca falloff
	vec4  grade1;           // exposure, contrast, saturation, temperature
	vec4  grade2;           // tint, reserved...
	vec4  bloom;            // composite intensity, reserved...
} c;

float hash21(vec2 p)
{
	p = fract(p * vec2(443.8975, 397.2973));
	p += dot(p, p.yx + 19.19);
	return fract(p.x * p.y);
}

// FXAA 3.11 (the console variant): nine samples, no history — see postfx.hlsl
float fxaa_luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
vec3 fxaa(vec2 uv, vec2 texel)
{
	vec3 rgbM = texture(u_src, uv).rgb;
	vec3 rgbNW = texture(u_src, uv + vec2(-1.0, -1.0) * texel).rgb;
	vec3 rgbNE = texture(u_src, uv + vec2(1.0, -1.0) * texel).rgb;
	vec3 rgbSW = texture(u_src, uv + vec2(-1.0, 1.0) * texel).rgb;
	vec3 rgbSE = texture(u_src, uv + vec2(1.0, 1.0) * texel).rgb;
	float lumaM = fxaa_luma(rgbM), lumaNW = fxaa_luma(rgbNW), lumaNE = fxaa_luma(rgbNE), lumaSW = fxaa_luma(rgbSW), lumaSE = fxaa_luma(rgbSE);
	float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
	float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
	vec2 dir;
	dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
	dir.y = ((lumaNW + lumaSW) - (lumaNE + lumaSE));
	float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * (1.0 / 8.0)), 1.0 / 128.0);
	float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
	dir = min(vec2(8.0), max(vec2(-8.0), dir * rcpDirMin)) * texel;
	vec3 rgbA = 0.5 * (texture(u_src, uv + dir * (1.0 / 3.0 - 0.5)).rgb + texture(u_src, uv + dir * (2.0 / 3.0 - 0.5)).rgb);
	vec3 rgbB = rgbA * 0.5 + 0.25 * (texture(u_src, uv + dir * -0.5).rgb + texture(u_src, uv + dir * 0.5).rgb);
	float lumaB = fxaa_luma(rgbB);
	return (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
}

void main()
{
	vec2 uv = v_uv;
	vec3 col;
	if ((c.flags & 4u) != 0u)
	{
		vec2 from_c = uv - 0.5;
		float r = saturate(length(from_c) * 2.0);
		float amt = c.grain_ca.z * 0.01 * pow(r, max(c.grain_ca.w, 0.01));
		col.r = texture(u_src, uv + from_c * amt).r;
		col.g = texture(u_src, uv).g;
		col.b = texture(u_src, uv - from_c * amt).b;
	}
	else if ((c.flags & 64u) != 0u)
	{
		col = fxaa(uv, c.texel_size);   // anti-aliasing on the LDR picture
	}
	else
	{
		col = texture(u_src, uv).rgb;
	}

	if ((c.flags & 32u) != 0u)
		col += texture(u_bloom, uv).rgb * c.bloom.x;

	if ((c.flags & 2u) != 0u)
	{
		vec2 cell = floor(gl_FragCoord.xy / max(c.grain_ca.y, 1.0));
		float n = hash21(cell + fract(c.time * vec2(17.131, 3.7171)) * 289.17) * 2.0 - 1.0;
		float luma = dot(col, vec3(0.299, 0.587, 0.114));
		col = saturate(col + n * c.grain_ca.x * 0.25 * (1.0 - saturate(luma)));
	}

	if ((c.flags & 16u) != 0u)
	{
		col *= exp2(c.grade1.x);
		vec3 wb = vec3(1.0 + c.grade1.w * 0.2, 1.0 + c.grade2.x * 0.2, 1.0 - c.grade1.w * 0.2);
		col *= wb;
		col = (col - 0.5) * max(c.grade1.y, 0.0) + 0.5;
		float luma = dot(col, vec3(0.299, 0.587, 0.114));
		col = mix(vec3(luma), col, c.grade1.z);
		col = max(col, 0.0);
	}

	if ((c.flags & 1u) != 0u)
	{
		float aspect = c.texel_size.y / c.texel_size.x;
		vec2 d = (uv - 0.5) * 2.0 * c.vignette.x;
		d.x *= mix(1.0, aspect, c.vignette.z);
		float vig = pow(saturate(1.0 - dot(d, d)), c.vignette.y * 4.0 + 0.001);
		col = mix(c.vignette_color.rgb, col, vig);
	}

	if ((c.flags & 8u) != 0u)
		col = 1.0 - col;

	o_color = vec4(col, 1.0);
}
