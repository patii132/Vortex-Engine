#version 450
#extension GL_GOOGLE_include_directive : require
// Water shader (#200) — Vulkan twin of water.hlsl / water.metal for the custom material-shader route (one file, compiled
// per stage with -DVORTEX_VERTEX_STAGE / -DVORTEX_FRAGMENT_STAGE). The surface mesh carries the ground depth in uv.x;
// see water.hlsl for the per-material inputs.

#define PI 3.14159265359
#ifdef VORTEX_VERTEX_STAGE
#define SET_UNIFORM 1
#include "standard_common.glsl"
#include "standard_vsout.glsl"

layout(location = 0) in vec3 a_pos;
layout(location = 1) in vec3 a_norm;
layout(location = 2) in vec2 a_uv;
layout(location = 3) in vec4 a_iw0;
layout(location = 4) in vec4 a_iw1;
layout(location = 5) in vec4 a_iw2;
layout(location = 6) in vec4 a_iw3;

void main()
{
	vec4 tint = vec4(1.0 + a_iw0.w, 1.0 + a_iw1.w, 1.0 + a_iw2.w, a_iw3.w);
	mat4 world = mat4(vec4(a_iw0.xyz, 0.0), vec4(a_iw1.xyz, 0.0), vec4(a_iw2.xyz, 0.0), vec4(a_iw3.xyz, 1.0));
	vec4 world_pos = world * vec4(a_pos, 1.0);
	v_world_pos = world_pos.xyz;
	gl_Position = frame.view_projection * world_pos;
	mat3 world3 = mat3(a_iw0.xyz, a_iw1.xyz, a_iw2.xyz);
	v_norm = normalize(world3 * a_norm);
	v_uv = a_uv;
	v_tangent = vec3(1.0, 0.0, 0.0);
	v_bitangent = vec3(0.0, 0.0, 1.0);
	v_tint = tint;
}

#else

#define SET_UNIFORM 3
#define VORTEX_NEED_PER_OBJECT
#define VORTEX_NEED_LIGHTS
#define VORTEX_VSOUT_IN
#include "common.glsl"
#include "standard_common.glsl"
#include "standard_vsout.glsl"

layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D       u_albedo;
layout(set = 2, binding = 1) uniform sampler2D       u_normal;
layout(set = 2, binding = 2) uniform sampler2D       u_metallic;
layout(set = 2, binding = 3) uniform sampler2D       u_roughness;
layout(set = 2, binding = 4) uniform sampler2D       u_ao;
layout(set = 2, binding = 5) uniform sampler2D       u_height;
layout(set = 2, binding = 6) uniform sampler2DShadow u_spot_shadow;
layout(set = 2, binding = 7) uniform sampler2DShadow u_csm_shadow;
layout(set = 2, binding = 8) uniform sampler2DShadow u_point_shadow;
layout(set = 2, binding = 9) uniform sampler2D       u_ssao;
layout(set = 2, binding = 10) uniform sampler2D      u_env;    // the sky as a reflection (IBL step 2)
layout(set = 2, binding = 11) uniform sampler2D      u_scene_color;   // the opaque colour so far (#200)
layout(set = 2, binding = 12) uniform sampler2D      u_scene_depth;   // + its depth (window z)

float fog_optical_depth(float density, float height_y, float k, vec3 cam, vec3 world_pos)
{
	vec3 delta = world_pos - cam;
	float dist = length(delta);
	if (k <= 0.0) { float d = density * dist; return -log2(max(exp2(-d * d), 1e-6)); }
	float ya = cam.y - height_y, yb = world_pos.y - height_y;
	if (ya <= 0.0 && yb <= 0.0) return density * dist;
	float t0 = 0.0, t1 = 1.0;
	if (ya <= 0.0) t0 = -ya / (yb - ya);
	else if (yb <= 0.0) t1 = ya / (ya - yb);
	float above = (t1 - t0) * dist, below = dist - above;
	float hya = max(ya, 0.0), hyb = max(yb, 0.0), dyv = hyb - hya;
	float mean_density = abs(dyv) > 1e-3 ? (exp(-k * hya) - exp(-k * hyb)) / (k * dyv) : exp(-k * hya);
	return density * (below + above * mean_density);
}

// Aerial perspective: the flat fog colour plus the sun's forward-scatter glow when looking towards it (fog_sun_glow 0 = off).
vec3 fog_inscatter(vec3 world_pos)
{
	vec3 c = frame.fog_color;
	if (frame.fog_sun_glow > 0.0 && frame.directional_intensity > 0.001)
	{
		vec3 V = normalize(world_pos - frame.camera_position);
		float mu = saturate(dot(V, normalize(-frame.light_direction)));
		float phase = 0.12 * mu * mu + 0.88 * pow(mu, 10.0);
		c += frame.light_color * (frame.fog_sun_glow * phase);
	}
	return c;
}

vec3 apply_fog(vec3 color, vec3 world_pos)
{
	if (frame.fog_density <= 0.0) return color;
	float optical = fog_optical_depth(frame.fog_density, frame.fog_height_y, frame.fog_height_falloff, frame.camera_position, world_pos);
	float f = 1.0 - exp2(-optical);
	return mix(color, fog_inscatter(world_pos), saturate(f));
}

float sample_cascade_shadow(vec3 wp)
{
	int count = int(lights.dir_shadow_params.z);
	if (count <= 0) return 1.0;
	for (int c = 0; c < 3; ++c)
	{
		if (c >= count) break;
		vec4 sp = lights.cascade_vp[c] * vec4(wp, 1.0);
		if (sp.w <= 0.0) continue;
		vec3 ndc = sp.xyz / sp.w;
		vec2 suv = ndc.xy * vec2(0.5, -0.5) + 0.5;
		if (min(suv.x, suv.y) < 0.02 || max(suv.x, suv.y) > 0.98 || ndc.z > 1.0 || ndc.z < 0.0) continue;
		float t = frame.shadow_map_texel;
		suv = clamp(suv, vec2(t * 1.5), vec2(1.0 - t * 1.5)) * 0.5;
		suv += vec2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
		float bias = lights.dir_shadow_params.y * (1.0 + float(c));
		float atlas_texel = t * 0.5;
		float lit = 0.0;
		for (int y = -1; y <= 1; ++y)
			for (int x = -1; x <= 1; ++x)
				lit += texture(u_csm_shadow, vec3(suv + vec2(float(x), float(y)) * atlas_texel, ndc.z - bias));
		return mix(1.0, lit / 9.0, saturate(lights.dir_shadow_params.x));
	}
	return 1.0;
}

vec3 wave_normal(vec2 p, float t, float height)
{
	vec2 d1 = normalize(vec2(1.0, 0.6)), d2 = normalize(vec2(-0.7, 1.0)), d3 = normalize(vec2(0.3, -1.0)), d4 = normalize(vec2(-1.0, -0.4));
	float a1 = 0.55, a2 = 0.35, a3 = 0.22, a4 = 0.10;
	float f1 = 1.0, f2 = 1.7, f3 = 2.9, f4 = 7.3;
	float s1 = 0.9, s2 = 1.3, s3 = 1.9, s4 = 3.1;
	float c1 = cos(dot(d1, p) * f1 + t * s1), c2 = cos(dot(d2, p) * f2 + t * s2), c3 = cos(dot(d3, p) * f3 + t * s3), c4 = cos(dot(d4, p) * f4 + t * s4);
	float dx = a1 * f1 * d1.x * c1 + a2 * f2 * d2.x * c2 + a3 * f3 * d3.x * c3 + a4 * f4 * d4.x * c4;
	float dz = a1 * f1 * d1.y * c1 + a2 * f2 * d2.y * c2 + a3 * f3 * d3.y * c3 + a4 * f4 * d4.y * c4;
	return normalize(vec3(-dx * height, 1.0, -dz * height));
}

float hash2(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise2(vec2 p)
{
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash2(i), hash2(i + vec2(1, 0)), f.x), mix(hash2(i + vec2(0, 1)), hash2(i + vec2(1, 1)), f.x), f.y);
}

// The sky as a reflection (IBL step 2): an equirect of the sky with mips at binding 10, env_sky.w = 2 while bound,
// env_horizon.w = its mip count; the baker's 8-bit soft compression (straight below 1, x / (1 + x) above) undone here.
vec2 env_uv(vec3 d)
{
	return vec2(atan(d.x, d.z) / (2.0 * 3.14159265) + 0.5, acos(clamp(d.y, -1.0, 1.0)) / 3.14159265);
}
vec3 decode_env(vec3 c)
{
	vec3 lo = c / 0.75;
	vec3 t = clamp((c - 0.75) / 0.25, 0.0, 1.0);
	vec3 hi = 1.0 / max(1.0 - t, vec3(0.0625));
	return (c.x <= 0.75 && c.y <= 0.75 && c.z <= 0.75) ? lo : max(lo, hi);
}
vec3 sample_env(vec3 R, float roughness, float mips, float lod_min)
{
	float lod = max(clamp(roughness, 0.0, 1.0) * max(mips - 1.0, 0.0), lod_min);
	return decode_env(textureLod(u_env, env_uv(R), lod).rgb);
}

// Window z -> view-space distance for this view's perspective projection (near / far in camera_clip).
float linear_depth(float z)
{
	float n = frame.camera_clip.x, f = frame.camera_clip.y;
	return n * f / max(f - z * (f - n), 1e-4);
}

// Screen-space reflection (#200): the reflected view ray, projected to the picture and walked there in even steps
// (1 / w interpolates linearly along a straight line, so each sample's view depth is exact); where the ray dips behind
// the opaque depth copy by less than a thickness that grows with distance, a few bisection steps settle the hit. The
// hit's screen UV comes back with a weight that fades at the picture's edges; 0 = nothing (the sky map takes over).
float ssr_trace(vec3 origin, vec3 dir, float cam_dist, out vec2 hit_uv)
{
	hit_uv = vec2(0.0);
	// orthographic views (the w column of a perspective view-projection carries the forward vector, an orthographic one
	// is all zero there) and rays into the water: nothing
	if (abs(frame.view_projection[0][3]) + abs(frame.view_projection[1][3]) + abs(frame.view_projection[2][3]) < 1e-5 || dir.y <= 0.02) return 0.0;
	vec2 dim = vec2(textureSize(u_scene_depth, 0));
	vec4 c0 = frame.view_projection * vec4(origin, 1.0);
	vec4 cd = frame.view_projection * vec4(dir, 0.0);
	float L = 300.0;
	if (c0.w + L * cd.w < 0.2) L = (0.2 - c0.w) / cd.w;   // towards the camera: stop in front of the near plane
	if (L <= 0.05) return 0.0;
	vec4 c1 = c0 + L * cd;
	vec2 uv0 = vec2(0.5 + 0.5 * c0.x / c0.w, 0.5 - 0.5 * c0.y / c0.w);
	vec2 uv1 = vec2(0.5 + 0.5 * c1.x / c1.w, 0.5 - 0.5 * c1.y / c1.w);
	vec2 d = uv1 - uv0;
	float t_end = 1.0;
	if (d.x > 0.0) t_end = min(t_end, (1.0 - uv0.x) / d.x); else if (d.x < 0.0) t_end = min(t_end, -uv0.x / d.x);
	if (d.y > 0.0) t_end = min(t_end, (1.0 - uv0.y) / d.y); else if (d.y < 0.0) t_end = min(t_end, -uv0.y / d.y);
	if (t_end <= 0.0) return 0.0;
	float inv_w0 = 1.0 / c0.w, inv_w1 = 1.0 / c1.w;
	float t_prev = 0.0;
	for (int i = 1; i <= 48; ++i)
	{
		float t = t_end * float(i) / 48.0;
		vec2 uv = uv0 + d * t;
		float w = 1.0 / mix(inv_w0, inv_w1, t);
		float zs = linear_depth(texelFetch(u_scene_depth, ivec2(uv * dim), 0).r);
		if (zs < w - 0.02 && w - zs < max(0.4, 0.06 * w))
		{
			float a = t_prev, b = t;
			for (int k = 0; k < 4; ++k)
			{
				float m = 0.5 * (a + b);
				float wm = 1.0 / mix(inv_w0, inv_w1, m);
				float zm = linear_depth(texelFetch(u_scene_depth, ivec2((uv0 + d * m) * dim), 0).r);
				if (zm < wm) b = m; else a = m;
			}
			vec2 uvh = uv0 + d * b;
			hit_uv = uvh;
			vec2 e = min(uvh, 1.0 - uvh);
			return clamp(min(e.x, e.y) * 10.0, 0.0, 1.0);
		}
		t_prev = t;
	}
	return 0.0;
}

void main()
{
	float depth = max(v_uv.x, 0.0);
	vec3 deep = obj.base_color.rgb, shallow = v_tint.rgb;
	float reflection = saturate(obj.base_color.a);
	float absorb = max(obj.normal_strength, 0.1);
	float wave_scale = max(obj.uv_tiling.x, 0.1), wave_speed = max(obj.uv_tiling.y, 0.0);
	float t = obj.height_scale * wave_speed;
	float foam_width = obj.emissive_strength;
	float rough = clamp(obj.roughness, 0.01, 1.0);
	vec3 cam_pos = frame.camera_position;

	vec2 p = v_world_pos.xz / wave_scale;
	vec3 N = wave_normal(p, t, obj.alpha_cutoff);
	// the ripples fade with distance: beyond ~80 m the per-pixel sine slopes only alias into stripes
	float cam_dist = length(cam_pos - v_world_pos);
	N = normalize(mix(vec3(0.0, 1.0, 0.0), N, clamp(80.0 / max(cam_dist, 1.0), 0.0, 1.0)));
	vec3 V = normalize(cam_pos - v_world_pos);
	float NdotV = max(dot(N, V), 0.0);
	float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

	float body = 1.0 - exp(-depth / absorb);
	vec3 water = mix(shallow, deep, body);

	vec3 sky_color = frame.env_sky.w > 0.5 ? frame.env_sky.rgb : vec3(0.5, 0.55, 0.7);
	vec3 horizon = frame.env_sky.w > 0.5 ? frame.env_horizon.rgb : vec3(0.6, 0.62, 0.68);
	vec3 L = normalize(-frame.light_direction);
	float shadow = sample_cascade_shadow(v_world_pos);
	vec3 sun = frame.light_color * frame.directional_intensity * shadow;
	vec3 lit = water * (horizon * frame.ambient_strength * 1.2 + sun * (0.12 + 0.28 * saturate(dot(N, L))));

	vec3 R = reflect(-V, N);
	const bool have_scene = textureSize(u_scene_color, 0).x > 1;   // the white 1 px texture stands in before the copies exist
	vec2 ssr_uv = vec2(0.0);
	float ssr_w = have_scene ? ssr_trace(v_world_pos, R, cam_dist, ssr_uv) : 0.0;
	vec3 sky_refl;
	if (frame.env_sky.w > 1.5)
	{
		// the real sky in the water: never below the horizon (the picture's ground is not the lake's surroundings), and
		// blurred with distance — far away the per-pixel ripple slopes would only turn the sky's sharp horizon into stripes
		float lod_min = 4.0 * clamp((cam_dist - 40.0) / 160.0, 0.0, 1.0);
		sky_refl = sample_env(vec3(R.x, max(R.y, 0.02), R.z), rough, frame.env_horizon.w, lod_min);
	}
	else if (frame.env_sky.w > 0.5)
		sky_refl = R.y >= 0.0 ? mix(frame.env_horizon.rgb, frame.env_sky.rgb, pow(saturate(R.y), 0.5)) : mix(frame.env_horizon.rgb, frame.env_ground.rgb, pow(saturate(-R.y), 0.6));
	else
		sky_refl = mix(horizon, sky_color, saturate(R.y)) * 0.8;
	sky_refl *= max(frame.ambient_strength, 0.3) * 1.3;
	vec3 color = mix(lit, sky_refl, fresnel * reflection * (1.0 - ssr_w));

	vec3 H = normalize(V + L);
	float spec_pow = mix(1400.0, 24.0, rough);
	float spec = pow(max(dot(N, H), 0.0), spec_pow) * (spec_pow + 8.0) / (8.0 * PI) * 0.06;
	color += sun * spec * mix(0.4, 1.0, fresnel);

	float foam = 0.0;
	if (foam_width > 0.0)
	{
		float n = noise2(p * 6.0 + vec2(t * 0.35, -t * 0.2)) * 0.6 + noise2(p * 17.0 - vec2(t * 0.5, t * 0.3)) * 0.4;
		float band = saturate(1.0 - depth / foam_width);
		foam = saturate(band * band * (0.35 + 0.9 * n) * 1.4);
	}
	vec3 foam_color = vec3(0.92, 0.94, 0.92) * (horizon * frame.ambient_strength + sun * 0.9);
	color = mix(color, foam_color, foam * 0.85);

	float alpha = saturate(depth / 0.3) * mix(0.5, 0.97, saturate(depth / absorb));
	alpha = max(alpha, foam * 0.95 * saturate(depth / 0.08));

	color = apply_fog(color, v_world_pos);
	color = tonemap_scene(color, frame.scene_exposure, frame.tonemapper);
	color = pow(max(color, 0.0), vec3(1.0 / 2.2));
	if (have_scene)
	{
		// Refraction (#200): the opaque picture behind the surface, bent by the ripples (less in the shallows and far away),
		// unless the bent sample lies in front of the surface — then the straight one. Composed here instead of the blend,
		// and the screen-space reflection laid over it where the march hit.
		vec2 dim = vec2(textureSize(u_scene_color, 0));
		vec2 uv = gl_FragCoord.xy / dim;
		vec2 ruv = clamp(uv + N.xz * 0.12 * clamp(depth / 0.6, 0.0, 1.0) * clamp(40.0 / max(cam_dist, 1.0), 0.0, 1.0), vec2(0.5) / dim, 1.0 - vec2(0.5) / dim);
		if (texelFetch(u_scene_depth, ivec2(ruv * dim), 0).r < gl_FragCoord.z) ruv = uv;
		vec3 behind = textureLod(u_scene_color, ruv, 0.0).rgb;
		vec3 outc = mix(behind, color, alpha);
		if (ssr_w > 0.0)
			outc = mix(outc, textureLod(u_scene_color, ssr_uv, 0.0).rgb, ssr_w * fresnel * reflection * (1.0 - foam) * alpha);
		o_color = vec4(outc, 1.0);
		return;
	}
	o_color = vec4(color, alpha);
}

#endif
