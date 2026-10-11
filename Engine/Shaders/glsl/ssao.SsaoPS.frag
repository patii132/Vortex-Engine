#version 450
#extension GL_GOOGLE_include_directive : require
// SSAO (ssao.metal SsaoPS): Alchemy-style AO over the half-res depth prepass. The depth target is bound as an
// ordinary sampled texture here (no comparison sampler), so it reads as a plain sampler2D.
#include "common.glsl"

layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_depth;
layout(set = 2, binding = 1) uniform sampler2D u_prev_color;   // the previous frame's opaque colour (LDR)
layout(set = 2, binding = 2) uniform sampler2D u_csm;          // the sun's cascade atlas (depth as a plain texture) — the bounce's emitters

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
	// the bounce from the sun's shadow map (#257 step 2, a reflective shadow map read from depth alone) — appended @240
	mat4  view_to_rsm;       // @240 view -> the bounce cascade's clip
	mat4  rsm_to_view;       // @304 the bounce cascade's clip -> view
	vec4  rsm_tile;          // @368 xy the cascade's tile offset in the atlas (uv), z the tile scale, w one atlas texel (uv)
	vec4  rsm_sun_view;      // @384 xyz towards the sun in view space, w the gather radius in tile uv
	vec4  rsm_flux;          // @400 rgb ground albedo x sun colour x intensity, w the gather radius (m); 0 = off
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
	// ---- the bounce from the sun's shadow map (#257 step 2): every point the sun sees is a Lambertian emitter of the ground's
	// colour; a disk of them around the receiver in the cascade is gathered with the full geometric term, their normals from
	// the map's own depth gradient. Off-screen sand lights the shaded floor and the trunks — what the screen trace cannot see.
	if (c.gi_strength > 0.0 && c.rsm_flux.w > 0.0)
	{
		vec4 lp = c.view_to_rsm * vec4(P, 1.0);
		if (lp.w > 0.001)
		{
			vec2 tuv = (lp.xy / lp.w) * vec2(0.5, -0.5) + 0.5;
			if (tuv.x > 0.02 && tuv.x < 0.98 && tuv.y > 0.02 && tuv.y < 0.98)
			{
				vec3 L = c.rsm_sun_view.xyz;
				float R = c.rsm_sun_view.w, Rm = c.rsm_flux.w;
				const int K = 12;
				float area = 3.14159265 * Rm * Rm / float(K);
				float tx = c.rsm_tile.w / c.rsm_tile.z;   // one atlas texel in tile uv
				vec3 E = vec3(0.0);
				float ang = hash12(gl_FragCoord.xy * 1.7 + 3.1) * 6.2831853;
				for (int k = 0; k < K; ++k)
				{
					float a = ang + float(k) * 2.3999632;
					float r = R * sqrt((float(k) + 0.5) / float(K));
					vec2 uvk = tuv + vec2(cos(a), sin(a)) * r;
					vec2 auv = uvk * c.rsm_tile.z + c.rsm_tile.xy;
					float dk = texture(u_csm, auv).r;
					if (dk >= 0.9999) continue;
					vec2 nk = uvk * vec2(2.0, -2.0) + vec2(-1.0, 1.0);
					vec4 vx = c.rsm_to_view * vec4(nk, dk, 1.0);
					vec3 X = vx.xyz / vx.w;
					float dkx = texture(u_csm, auv + vec2(c.rsm_tile.w, 0.0)).r;
					float dky = texture(u_csm, auv + vec2(0.0, c.rsm_tile.w)).r;
					vec4 vxx = c.rsm_to_view * vec4(nk + vec2(2.0 * tx, 0.0), dkx, 1.0);
					vec4 vxy = c.rsm_to_view * vec4(nk - vec2(0.0, 2.0 * tx), dky, 1.0);
					vec3 Nk = cross(vxx.xyz / vxx.w - X, vxy.xyz / vxy.w - X);
					float nl = dot(Nk, Nk);
					if (nl < 1e-12) continue;
					Nk *= inversesqrt(nl);
					if (dot(Nk, L) < 0.0) Nk = -Nk;   // the sun sees it, so it faces the sun
					vec3 w = X - P;
					float d2 = max(dot(w, w), 0.25);
					w *= inversesqrt(d2);
					float g = max(dot(N, w), 0.0) * max(dot(Nk, -w), 0.0) * max(dot(Nk, L), 0.0);
					E += g * (area / d2);
				}
				gi += c.rsm_flux.rgb * E * (c.gi_strength / 3.14159265);
			}
		}
	}
	o_color = vec4(sqrt(saturate(gi * 0.25)), ao);
}
