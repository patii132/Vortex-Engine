#version 450
// Terrain shader (#124) — Vulkan twin of terrain.hlsl / terrain.metal for the SDL GPU backend's custom material-shader
// route: glslc compiles this ONE file twice, with -DVORTEX_VERTEX_STAGE and -DVORTEX_FRAGMENT_STAGE. Self-contained
// (no includes): the uniform blocks are byte-matched to standard_common.glsl, the texture slots to standard.PSMain.frag
// (0 albedo atlas, 1 normal atlas, 2 splat map, 3 roughness atlas, 6..8 shadow atlases, 9 SSAO).
// Resource sets follow SDL GPU's SPIR-V rules: vertex uniforms set 1, fragment textures set 2, fragment uniforms set 3.

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

#ifdef VORTEX_VERTEX_STAGE
#define SET_UNIFORM 1
#else
#define SET_UNIFORM 3
#endif

layout(set = SET_UNIFORM, binding = 0, std140) uniform PerFrameBlock
{
	mat4  view_projection;
	vec3  camera_position;
	float padding0;
	vec3  light_direction;
	float directional_intensity;
	vec3  light_color;
	float ambient_strength;
	uint  point_light_count;
	uint  spot_light_count;
	uint  frame_padding0;
	uint  frame_padding1;
	vec3  fog_color;
	float fog_density;
	float fog_height_y;
	float fog_height_falloff;
	uint  fog_mode;
	float fog_padding;
	float shadow_map_texel;
	uint  shadow_padding0;
	uint  shadow_padding1;
	uint  shadow_padding2;
	float ssao_enabled;
	float ssao_padding0;
	float ssao_padding1;
	float ssao_padding2;
	vec4  env_sky;
	vec4  env_horizon;
	vec4  env_ground;
} frame;

#ifdef VORTEX_VERTEX_STAGE

layout(location = 0) in vec3 a_pos;
layout(location = 1) in vec3 a_norm;
layout(location = 2) in vec2 a_uv;
layout(location = 3) in vec4 a_iw0;
layout(location = 4) in vec4 a_iw1;
layout(location = 5) in vec4 a_iw2;
layout(location = 6) in vec4 a_iw3;

layout(location = 0) out vec3 v_world_pos;
layout(location = 1) out vec3 v_norm;
layout(location = 2) out vec2 v_uv;
layout(location = 3) out vec3 v_tangent;
layout(location = 4) out vec3 v_bitangent;
layout(location = 5) out vec4 v_tint;

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
	vec3 c = cross(v_norm, vec3(0.0, 1.0, 0.0));
	v_tangent = (dot(c, c) < 1e-6) ? normalize(cross(v_norm, vec3(1.0, 0.0, 0.0))) : normalize(c);
	v_bitangent = normalize(cross(v_norm, v_tangent));
	v_tint = tint;
}

#else // VORTEX_FRAGMENT_STAGE

layout(set = SET_UNIFORM, binding = 1, std140) uniform PerObjectBlock
{
	mat4  world;
	vec4  base_color;             // terrain: tiles per metre of layers 0..3
	float metallic;
	float roughness;              // terrain: the roughness when no roughness atlas is bound
	float ao;
	float normal_strength;
	uint  has_albedo_texture;
	uint  has_normal_texture;
	uint  has_metallic_texture;   // terrain: the splat map is bound
	uint  has_roughness_texture;
	uint  has_ao_texture;
	uint  use_directx_normals;
	uint  is_unlit;
	float emissive_strength;
	vec2  uv_tiling;
	uint  has_height_texture;
	float height_scale;
	float alpha_cutoff;
	float _pad0; float _pad1; float _pad2;
} obj;

struct PointLight
{
	vec3  position; float range;
	vec3  color;    float intensity;
};

struct SpotLight
{
	vec3  position;  float range;
	vec3  direction; float spot_angle;
	vec3  color;     float intensity;
	float inner_spot_angle;
	float shadow_strength;
	float shadow_bias;
	float shadow_slot;
};

layout(set = SET_UNIFORM, binding = 2, std140) uniform LightBufferBlock
{
	PointLight point_lights[MAX_POINT_LIGHTS];
	SpotLight  spot_lights[MAX_SPOT_LIGHTS];
	mat4       shadow_vp[4];
	mat4       cascade_vp[3];
	vec4       cascade_splits;
	vec4       dir_shadow_params;
	vec4       point_shadows[2];
	mat4       point_face_vp[12];
	vec4       sky_sh[9];                       // @2304  sky light: SH9 irradiance per channel, w of [0] = on
} lights;

layout(location = 0) in vec3 v_world_pos;
layout(location = 1) in vec3 v_norm;
layout(location = 2) in vec2 v_uv;
layout(location = 3) in vec3 v_tangent;
layout(location = 4) in vec3 v_bitangent;
layout(location = 5) in vec4 v_tint;

layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D       u_albedo;
layout(set = 2, binding = 1) uniform sampler2D       u_normal;
layout(set = 2, binding = 2) uniform sampler2D       u_splat;
layout(set = 2, binding = 3) uniform sampler2D       u_roughness;
layout(set = 2, binding = 4) uniform sampler2D       u_ao;
layout(set = 2, binding = 5) uniform sampler2D       u_height;
layout(set = 2, binding = 6) uniform sampler2DShadow u_spot_shadow;
layout(set = 2, binding = 7) uniform sampler2DShadow u_csm_shadow;
layout(set = 2, binding = 8) uniform sampler2DShadow u_point_shadow;
layout(set = 2, binding = 9) uniform sampler2D       u_ssao;

float  saturate(float x) { return clamp(x, 0.0, 1.0); }
vec2   saturate(vec2  x) { return clamp(x, vec2(0.0), vec2(1.0)); }
vec3   saturate(vec3  x) { return clamp(x, vec3(0.0), vec3(1.0)); }

float d_ggx(float NdotH, float roughness)
{
	float a = roughness * roughness;
	float a2 = a * a;
	float d = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
	return a2 / (PI * d * d + 0.0001);
}

float g_schlick_ggx(float NdotV, float roughness)
{
	float k = (roughness + 1.0);
	k = (k * k) / 8.0;
	return NdotV / (NdotV * (1.0 - k) + k + 0.0001);
}

float g_smith(float NdotV, float NdotL, float roughness)
{
	return g_schlick_ggx(NdotV, roughness) * g_schlick_ggx(NdotL, roughness);
}

vec3 f_schlick(float VdotH, vec3 F0)
{
	return F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);
}

vec3 srgb_to_linear(vec3 color)
{
	return pow(max(color, 0.0), vec3(2.2));
}

float attenuation(float distance_, float range)
{
	float d = distance_ / range;
	float atten = saturate(1.0 - d * d);
	return atten * atten / (distance_ * distance_ + 0.01);
}

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

vec3 apply_fog(vec3 color, vec3 world_pos)
{
	if (frame.fog_density <= 0.0) return color;
	float optical = fog_optical_depth(frame.fog_density, frame.fog_height_y, frame.fog_height_falloff, frame.camera_position, world_pos);
	float f = 1.0 - exp2(-optical);
	return mix(color, frame.fog_color, saturate(f));
}

float sample_spot_shadow(vec3 wp, int slot, float strength, float bias)
{
	vec4 sp = lights.shadow_vp[slot] * vec4(wp, 1.0);
	if (sp.w <= 0.0) return 1.0;
	vec3 ndc = sp.xyz / sp.w;
	vec2 suv = ndc.xy * vec2(0.5, -0.5) + 0.5;
	if (any(notEqual(saturate(suv), suv)) || ndc.z > 1.0) return 1.0;
	float t = frame.shadow_map_texel;
	suv = clamp(suv, vec2(t * 0.5), vec2(1.0 - t * 0.5)) * 0.5;
	suv += vec2((slot & 1) != 0 ? 0.5 : 0.0, (slot & 2) != 0 ? 0.5 : 0.0);
	float lit = texture(u_spot_shadow, vec3(suv, ndc.z - bias));
	return mix(1.0, lit, saturate(strength));
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

float sample_point_shadow(vec3 wp, vec3 lpos, int light_index)
{
	for (int p = 0; p < 2; ++p)
	{
		if (int(lights.point_shadows[p].x) != light_index) continue;
		vec3 d = wp - lpos;
		vec3 ad = abs(d);
		int face;
		if (ad.x >= ad.y && ad.x >= ad.z) face = d.x > 0.0 ? 0 : 1;
		else if (ad.y >= ad.z)            face = d.y > 0.0 ? 2 : 3;
		else                              face = d.z > 0.0 ? 4 : 5;
		vec4 sp = lights.point_face_vp[p * 6 + face] * vec4(wp, 1.0);
		if (sp.w <= 0.0) return 1.0;
		vec3 ndc = sp.xyz / sp.w;
		vec2 suv = ndc.xy * vec2(0.5, -0.5) + 0.5;
		if (any(notEqual(saturate(suv), suv)) || ndc.z > 1.0) return 1.0;
		const float tile_texel = 1.0 / 1024.0;
		suv = clamp(suv, vec2(tile_texel * 1.5), vec2(1.0 - tile_texel * 1.5));
		int tile = p * 6 + face;
		vec2 auv = (suv + vec2(float(tile & 3), float(tile >> 2))) * vec2(0.25, 1.0 / 3.0);
		const vec2 atlas_texel = vec2(1.0 / 4096.0, 1.0 / 3072.0);
		float bias = lights.point_shadows[p].z;
		float lit = 0.0;
		for (int y = -1; y <= 1; ++y)
			for (int x = -1; x <= 1; ++x)
				lit += texture(u_point_shadow, vec3(auv + vec2(float(x), float(y)) * atlas_texel, ndc.z - bias));
		return mix(1.0, lit / 9.0, saturate(lights.point_shadows[p].y));
	}
	return 1.0;
}

vec2 atlas_uv(vec2 uv, int layer)
{
	const float pad = 1.0 / 128.0;
	vec2 q = vec2(float(layer & 1), float(layer >> 1));
	return (q + pad + fract(uv) * (1.0 - 2.0 * pad)) * 0.5;
}

void clamp_grad(inout vec2 dx, inout vec2 dy)
{
	const float max_g = 1.0 / 16.0;
	float gl = max(length(dx), length(dy));
	if (gl > max_g) { float s = max_g / gl; dx *= s; dy *= s; }
}

// Sky light (IBL step 1): E(n)/π from the SH9 irradiance baked off the sky picture (cosine-lobe weights 1, 2/3, 1/4)
vec3 sky_irradiance(vec3 n)
{
	float x = n.x, y = n.y, z = n.z;
	vec3 e = lights.sky_sh[0].rgb * 0.282095;
	e += (2.0 / 3.0) * (lights.sky_sh[1].rgb * (0.488603 * y) + lights.sky_sh[2].rgb * (0.488603 * z) + lights.sky_sh[3].rgb * (0.488603 * x));
	e += 0.25 * (lights.sky_sh[4].rgb * (1.092548 * x * y) + lights.sky_sh[5].rgb * (1.092548 * y * z) + lights.sky_sh[6].rgb * (0.315392 * (3.0 * z * z - 1.0))
	           + lights.sky_sh[7].rgb * (1.092548 * x * z) + lights.sky_sh[8].rgb * (0.546274 * (x * x - y * y)));
	return max(e, vec3(0.0));
}

// Macro variation: a 2-octave value noise over the world XZ scales the blended albedo by ±9 % (no extra samples)
float macro_hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float macro_noise(vec2 p)
{
	vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(macro_hash(i), macro_hash(i + vec2(1, 0)), f.x), mix(macro_hash(i + vec2(0, 1)), macro_hash(i + vec2(1, 1)), f.x), f.y);
}
float macro_variation(vec2 world_xz)
{
	float n = 0.65 * macro_noise(world_xz * 0.025) + 0.35 * macro_noise(world_xz * 0.11 + 7.3);
	return 0.91 + 0.18 * n;
}

void main()
{
	vec3 cam_pos = frame.camera_position;
	vec3 Ng = normalize(v_norm);

	vec4 w = obj.has_metallic_texture != 0u ? texture(u_splat, v_uv) : vec4(1.0, 0.0, 0.0, 0.0);
	float wsum = w.r + w.g + w.b + w.a;
	w = wsum > 1e-4 ? w / wsum : vec4(1.0, 0.0, 0.0, 0.0);

	vec2 p = v_world_pos.xz;
	vec4 tiles_per_metre = max(obj.base_color, vec4(1e-4));
	vec2 dpx = dFdx(p), dpy = dFdy(p);

	vec3 albedo = vec3(0.0);
	vec3 nm = vec3(0.0);
	float rough = 0.0;
	// detail: the layer's own normal map again at 7.3x finer tiling, fading out beyond ~28 m — close-up ripples and grain
	float detail_w = clamp(1.0 - length(frame.camera_position - v_world_pos) / 28.0, 0.0, 1.0);
	for (int i = 0; i < 4; ++i)
	{
		float wi = w[i];
		if (wi <= 0.002) continue;
		vec2 uv = p * tiles_per_metre[i];
		vec2 a = atlas_uv(uv, i);
		vec2 dx = dpx * tiles_per_metre[i] * 0.5, dy = dpy * tiles_per_metre[i] * 0.5;
		clamp_grad(dx, dy);
		albedo += wi * (obj.has_albedo_texture != 0u ? srgb_to_linear(textureGrad(u_albedo, a, dx, dy).rgb) : vec3(0.5));
		if (obj.has_normal_texture != 0u) nm += wi * (textureGrad(u_normal, a, dx, dy).rgb * 2.0 - 1.0);
		if (obj.has_normal_texture != 0u && detail_w > 0.001)
		{
			vec2 ad = atlas_uv(uv * 7.3 + 0.37, i);
			vec2 dxd = dx * 7.3, dyd = dy * 7.3;
			clamp_grad(dxd, dyd);
			vec3 nd = textureGrad(u_normal, ad, dxd, dyd).rgb * 2.0 - 1.0;
			nm += wi * detail_w * 0.6 * vec3(nd.xy, 0.0);
		}
		if (obj.has_roughness_texture != 0u) rough += wi * textureGrad(u_roughness, a, dx, dy).r;
	}
	albedo *= v_tint.rgb;
	albedo *= macro_variation(v_world_pos.xz);
	float roughness = max(obj.has_roughness_texture != 0u ? rough : obj.roughness, 0.04);
	float metallic = 0.0;
	float ao = obj.ao;

	vec3 N = Ng;
	if (obj.has_normal_texture != 0u && dot(nm, nm) > 1e-6)
	{
		if (obj.use_directx_normals == 0u) nm.y = -nm.y;
		nm.xy *= obj.normal_strength;
		vec3 T = normalize(vec3(1.0, 0.0, 0.0) - Ng * Ng.x);
		vec3 B = normalize(vec3(0.0, 0.0, 1.0) - Ng * Ng.z);
		N = normalize(nm.x * T + nm.y * B + max(nm.z, 0.05) * Ng);
	}

	vec3 V = normalize(cam_pos - v_world_pos);
	float NdotV = max(dot(N, V), 0.001);
	vec3 F0 = vec3(0.04);
	vec3 Lo = vec3(0.0);

	if (frame.directional_intensity > 0.001)
	{
		vec3 L = normalize(-frame.light_direction);
		vec3 H = normalize(V + L);
		float NdotL = max(dot(N, L), 0.0);
		float NdotH = max(dot(N, H), 0.0);
		float VdotH = max(dot(V, H), 0.0);
		float D = d_ggx(NdotH, roughness);
		float G = g_smith(NdotV, NdotL, roughness);
		vec3 F = f_schlick(VdotH, F0);
		vec3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
		vec3 kD = (1.0 - F) * (1.0 - metallic);
		vec3 radiance = frame.light_color * frame.directional_intensity;
		float sun_shadow = sample_cascade_shadow(v_world_pos);
		Lo += (kD * albedo / PI + spec) * radiance * NdotL * sun_shadow;
	}

	for (uint li = 0u; li < frame.point_light_count && li < uint(MAX_POINT_LIGHTS); ++li)
	{
		vec3 lpos = lights.point_lights[li].position;
		vec3 light_vec = lpos - v_world_pos;
		float dist = length(light_vec);
		if (dist < lights.point_lights[li].range)
		{
			vec3 L = light_vec / dist;
			vec3 H = normalize(V + L);
			float NdotL = max(dot(N, L), 0.0);
			float NdotH = max(dot(N, H), 0.0);
			float VdotH = max(dot(V, H), 0.0);
			float atten = attenuation(dist, lights.point_lights[li].range);
			atten *= sample_point_shadow(v_world_pos, lpos, int(li));
			vec3 radiance = lights.point_lights[li].color * lights.point_lights[li].intensity * atten;
			float D = d_ggx(NdotH, roughness);
			float G = g_smith(NdotV, NdotL, roughness);
			vec3 F = f_schlick(VdotH, F0);
			vec3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
			vec3 kD = (1.0 - F) * (1.0 - metallic);
			Lo += (kD * albedo / PI + spec) * radiance * NdotL;
		}
	}

	for (uint j = 0u; j < frame.spot_light_count && j < uint(MAX_SPOT_LIGHTS); ++j)
	{
		vec3 lpos = lights.spot_lights[j].position;
		vec3 light_vec = lpos - v_world_pos;
		float dist = length(light_vec);
		if (dist < lights.spot_lights[j].range)
		{
			vec3 L = light_vec / dist;
			vec3 spot_dir = normalize(lights.spot_lights[j].direction);
			float theta = dot(-L, spot_dir);
			float outer_cos = cos(lights.spot_lights[j].spot_angle * 0.5 * PI / 180.0);
			float inner_cos = cos(lights.spot_lights[j].inner_spot_angle * 0.5 * PI / 180.0);
			float spot_fade = saturate((theta - outer_cos) / (inner_cos - outer_cos + 0.001));
			if (theta > outer_cos)
			{
				vec3 H = normalize(V + L);
				float NdotL = max(dot(N, L), 0.0);
				float NdotH = max(dot(N, H), 0.0);
				float VdotH = max(dot(V, H), 0.0);
				float atten = attenuation(dist, lights.spot_lights[j].range) * spot_fade;
				if (lights.spot_lights[j].shadow_slot >= 0.0)
					atten *= sample_spot_shadow(v_world_pos, int(lights.spot_lights[j].shadow_slot),
						lights.spot_lights[j].shadow_strength, lights.spot_lights[j].shadow_bias);
				vec3 radiance = lights.spot_lights[j].color * lights.spot_lights[j].intensity * atten;
				float D = d_ggx(NdotH, roughness);
				float G = g_smith(NdotV, NdotL, roughness);
				vec3 F = f_schlick(VdotH, F0);
				vec3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
				vec3 kD = (1.0 - F) * (1.0 - metallic);
				Lo += (kD * albedo / PI + spec) * radiance * NdotL;
			}
		}
	}

	vec3 sky_color = vec3(0.5, 0.55, 0.7);
	vec3 ground_color = vec3(0.15, 0.15, 0.18);
	float sky_amount = dot(N, vec3(0.0, 1.0, 0.0)) * 0.5 + 0.5;
	vec3 hemisphere = mix(ground_color, sky_color, sky_amount);
	if (lights.sky_sh[0].w > 0.5) hemisphere = sky_irradiance(N);   // the real sky: blue from above, the ground's bounce from below
	vec3 ambient = hemisphere * frame.ambient_strength * albedo * ao;

	vec3 R = reflect(-V, N);
	vec3 env_color;
	if (frame.env_sky.w > 0.5)
	{
		vec3 sky_dir = R.y >= 0.0 ? mix(frame.env_horizon.rgb, frame.env_sky.rgb, pow(saturate(R.y), 0.6))
		                          : mix(frame.env_horizon.rgb, frame.env_ground.rgb, pow(saturate(-R.y), 0.6));
		vec3 sky_avg = (frame.env_sky.rgb + 2.0 * frame.env_horizon.rgb + frame.env_ground.rgb) * 0.25;
		env_color = mix(sky_dir, sky_avg, saturate(roughness * roughness * 1.5)) * frame.ambient_strength;
	}
	else
	{
		float up_factor = R.y * 0.5 + 0.5;
		env_color = mix(vec3(0.01, 0.01, 0.02), vec3(0.08, 0.10, 0.15), up_factor);
		env_color = mix(env_color, env_color * 0.2, roughness * roughness);
	}
	vec3 env_fresnel = F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(1.0 - NdotV, 5.0);
	ambient += env_color * env_fresnel * ao;

	if (frame.ssao_enabled > 0.5)
	{
		vec2 auv = gl_FragCoord.xy * vec2(frame.ssao_padding0, frame.ssao_padding1);
		ambient *= texture(u_ssao, auv).r;
	}

	vec3 color = ambient + Lo;
	color = apply_fog(color, v_world_pos);

	vec3 x = color * 0.5;
	vec3 a = x * (x + 0.0245786) - 0.000090537;
	vec3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
	color = saturate(a / b);
	color = pow(max(color, 0.0), vec3(1.0 / 2.2));
	o_color = vec4(color, 1.0);
}

#endif
