#version 450
#extension GL_GOOGLE_include_directive : require
// SSAO (ssao.metal SsaoPS): Alchemy-style AO over the half-res depth prepass. The depth target is bound as an
// ordinary sampled texture here (no comparison sampler), so it reads as a plain sampler2D.
#include "common.glsl"

layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_depth;
layout(set = 2, binding = 1) uniform sampler2D u_prev_color;   // the previous frame's opaque colour (LDR)

layout(set = 3, binding = 0, std140) uniform SsaoCB
{
	mat4  inv_proj;     // clip -> view (this view's projection only)
	vec2  texel;        // 1 / AO target size
	float radius;       // world-space sample radius
	float intensity;
	float bias;
	float proj_scale;   // 0.5 * proj._22 * targetHeight
	vec2  pad;
	mat4  proj;              // @96  view -> clip (screen-space GI, #257 step 1)
	mat4  view_to_prev_clip; // @160 view -> the previous frame's clip
	float gi_strength;       // @224
	float scene_exposure;
	float prev_valid;
	float gi_radius;
} c;

vec3 view_pos(vec2 uv)
{
	float d = texture(u_depth, uv).r;
	vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
	vec4 v = c.inv_proj * vec4(ndc, d, 1.0);
	return v.xyz / max(v.w, 1e-6);
}

float hash12(vec2 p)
{
	p = fract(p * vec2(443.8975, 397.2973));
	p += dot(p, p.yx + 19.19);
	return fract(p.x * p.y);
}

void main()
{
	float d = texture(u_depth, v_uv).r;
	if (d >= 0.9999) { o_color = vec4(1.0); return; }
	vec3 P = view_pos(v_uv);
	vec3 N = normalize(cross(dFdy(P), dFdx(P)));
	float pix_radius = clamp(c.proj_scale * c.radius / max(P.z, 0.1), 2.0, 64.0);
	const int TAPS = 12;
	float rot = hash12(gl_FragCoord.xy) * 6.2831853;
	float occlusion = 0.0;
	for (int k = 0; k < TAPS; ++k)
	{
		float a = rot + float(k) * 2.3999632;
		float r = pix_radius * sqrt((float(k) + 0.7) / float(TAPS));
		vec2 duv = vec2(cos(a), sin(a)) * r * c.texel;
		vec3 S = view_pos(v_uv + duv);
		vec3 v = S - P;
		occlusion += max(0.0, dot(v, N) - c.bias) / (dot(v, v) + 0.01);
	}
	float ao = saturate(1.0 - c.intensity * occlusion * (2.0 / float(TAPS)));

	// ---- screen-space GI: cosine-weighted rays against the same depth, radiance from the previous frame reprojected ----
	vec3 gi = vec3(0.0);
	if (c.gi_strength > 0.0 && c.prev_valid > 0.5)
	{
		vec3 up = abs(N.y) < 0.99 ? vec3(0, 1, 0) : vec3(1, 0, 0);
		vec3 T = normalize(cross(up, N)), B = cross(N, T);
		float h2 = hash12(gl_FragCoord.yx + 7.31);
		const int DIRS = 6, STEPS = 6;
		for (int k = 0; k < DIRS; ++k)
		{
			float u1 = fract(rot * 0.159155 + float(k) * 0.618034), u2 = fract(h2 + float(k) * 0.381966);
			float rr = sqrt(u1), ph = 6.2831853 * u2;
			vec3 dir = T * (rr * cos(ph)) + B * (rr * sin(ph)) + N * sqrt(max(0.0, 1.0 - u1));
			vec3 pos = P;
			float step = c.gi_radius / float(STEPS) * (0.6 + 0.8 * u2);
			for (int s = 0; s < STEPS; ++s)
			{
				pos += dir * step * (1.0 + 0.35 * float(s));
				vec4 cl = c.proj * vec4(pos, 1.0);
				if (cl.w <= 0.01) break;
				vec2 uv = vec2(cl.x / cl.w * 0.5 + 0.5, 0.5 - cl.y / cl.w * 0.5);
				if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;
				float sd = texture(u_depth, uv).r;
				if (sd >= 0.9999) continue;
				vec3 S = view_pos(uv);
				float dz = pos.z - S.z;
				if (dz > 0.03 && dz < step * 2.5)
				{
					if (dot(normalize(S - P), N) < 0.2) break;   // grazing the surface itself (a slope): not a bounce
					vec4 pc = c.view_to_prev_clip * vec4(S, 1.0);
					if (pc.w > 0.01)
					{
						vec2 puv = vec2(pc.x / pc.w * 0.5 + 0.5, 0.5 - pc.y / pc.w * 0.5);
						if (puv.x >= 0.0 && puv.x <= 1.0 && puv.y >= 0.0 && puv.y <= 1.0)
						{
							vec3 lin = pow(max(texture(u_prev_color, puv).rgb, 0.0), vec3(2.2));
							gi += lin / max(1.0 - 0.6 * lin, 0.25) / max(c.scene_exposure, 0.05);
						}
					}
					break;
				}
			}
		}
		gi *= c.gi_strength / float(DIRS);
		float gl = dot(gi, vec3(0.2126, 0.7152, 0.0722));
		gi = mix(vec3(gl), gi, 0.65);   // a bounce is never as saturated as the albedo squared suggests
	}
	o_color = vec4(sqrt(saturate(gi * 0.25)), ao);
}
