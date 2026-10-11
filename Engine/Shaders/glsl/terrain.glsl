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
	float scene_exposure;         // @156  pre-tonemap multiplier (0.5 = classic; <= 0 reads as 0.5)
	float shadow_map_texel;
	uint  tonemapper;             // 0 = ACES fit, 1 = PBR Neutral
	float gi_strength;            // screen-space GI: > 0 = u_ssao.rgb carries bounce light
	uint  shadow_padding2;
	float ssao_enabled;
	float ssao_padding0;
	float ssao_padding1;
	float fog_sun_glow;           // @188  the sun's glow in the fog (0 = off)
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
layout(set = 2, binding = 10) uniform sampler2D      u_env;    // the sky as a reflection (IBL step 2)

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

// Khronos PBR Neutral (the scene's tonemapper 1): hue-preserving with a gentle highlight desaturation. Input = the
// EXPOSED scene colour. (terrain.glsl carries its own helpers — no common.glsl here.)
vec3 tonemap_neutral(vec3 color)
{
	const float start_compression = 0.8 - 0.04;
	const float desaturation = 0.15;
	float x = min(color.r, min(color.g, color.b));
	float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
	color -= offset;
	float peak = max(color.r, max(color.g, color.b));
	if (peak < start_compression) return color;
	const float d = 1.0 - start_compression;
	float new_peak = 1.0 - d * d / (peak + d - start_compression);
	color *= new_peak / peak;
	float g = 1.0 - 1.0 / (desaturation * (peak - new_peak) + 1.0);
	return mix(color, vec3(new_peak), g);
}
vec3 tonemap_scene(vec3 color, float exposure, uint mode)
{
	vec3 x = color * (exposure > 0.0 ? exposure : 0.5);
	if (mode == 1u) return saturate(tonemap_neutral(max(x, 0.0)));
	vec3 a = x * (x + 0.0245786) - 0.000090537;
	vec3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
	return saturate(a / b);
}

// Aerial perspective: the flat fog colour plus the sun's forward-scatter glow when looking towards it (fog_sun_glow 0 = off).
// a normal-atlas texel: tangent-space xyz; a two-channel (BC5) atlas carries x / y and z is rebuilt per sample
vec3 decode_n(vec3 s)
{
	s = s * 2.0 - 1.0;
	if (obj.has_normal_texture == 2u) s.z = sqrt(saturate(1.0 - dot(s.xy, s.xy)));
	return s;
}

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

// The sun's shadow filter (Light ▸ Soft): softness <= 1 = the classic 3x3 box; above it a 16-tap Poisson disk of that
// radius in shadow texels — a real penumbra under palms instead of a stair-stepped edge.
const vec2 poisson16[16] = vec2[16](vec2(-0.94201624,-0.39906216), vec2(0.94558609,-0.76890725), vec2(-0.094184101,-0.92938870), vec2(0.34495938,0.29387760), vec2(-0.91588581,0.45771432), vec2(-0.81544232,-0.87912464), vec2(-0.38277543,0.27676845), vec2(0.97484398,0.75648379), vec2(0.44323325,-0.97511554), vec2(0.53742981,-0.47373420), vec2(-0.26496911,-0.41893023), vec2(0.79197514,0.19090188), vec2(-0.24188840,0.99706507), vec2(-0.81409955,0.91437590), vec2(0.19984126,0.78641367), vec2(0.14383161,-0.14100790));
float csm_pcf(vec2 suv, float depth, float atlas_texel, float softness, float zscale, float mpt)
{
	float lit = 0.0;
	if (softness <= 1.01)
	{
		for (int y = -1; y <= 1; ++y)
			for (int x = -1; x <= 1; ++x)
				lit += texture(u_csm_shadow, vec3(suv + vec2(float(x), float(y)) * atlas_texel, depth));
		return lit / 9.0;
	}
	// contact hardening (PCSS without a depth read): comparison taps at the receiver's depth minus 0 / 1 / 3 / 9 m over the
	// widest penumbra tell how far behind the receiver the blockers sit; the Poisson radius grows with that distance — a
	// trunk's shadow is sharp at its foot and soft where the crown throws it 10 m up. Softness caps the radius (texels).
	const float tan_a = 0.035;   // the sun's angular radius, widened to 2 degrees as the big engines do
	float rs = clamp(12.0 * tan_a / mpt, 1.0, softness) * atlas_texel;
	float sh[4];
	for (int l = 0; l < 4; ++l)
	{
	    float k = (l == 0) ? 0.0 : (l == 1 ? 1.0 : (l == 2 ? 3.0 : 9.0));
	    float s = 0.0;
	    for (int i = 0; i < 4; ++i) s += texture(u_csm_shadow, vec3(suv + poisson16[i * 4 + l] * rs, depth - k * zscale));
	    sh[l] = 1.0 - s * 0.25;
	}
	if (sh[0] < 0.02) return 1.0;   // nothing in the way anywhere in the search disk
	float bd = (0.5 * (sh[0] + sh[1]) + (sh[1] + sh[2]) + 3.0 * (sh[2] + sh[3])) / max(sh[0], 0.05);   // the mean blocker distance (m)
	float r = clamp(bd * tan_a / mpt, 1.0, softness) * atlas_texel;
	for (int i = 0; i < 16; ++i)
	    lit += texture(u_csm_shadow, vec3(suv + poisson16[i] * r, depth));
	return lit / 16.0;
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
		float guard = t * (max(lights.dir_shadow_params.w, 1.0) + 0.5);   // the whole filter stays inside the tile
		suv = clamp(suv, vec2(guard), vec2(1.0 - guard)) * 0.5;
		suv += vec2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
		float bias = lights.dir_shadow_params.y * (1.0 + float(c)) * (0.75 + 0.25 * max(lights.dir_shadow_params.w, 1.0));
		float atlas_texel = t * 0.5;
		mat4 M = lights.cascade_vp[c];
		float zscale = length(vec3(M[0][2], M[1][2], M[2][2]));                       // depth units per metre along the light
		float mpt = 2.0 * t / max(length(vec3(M[0][0], M[1][0], M[2][0])), 1e-6);   // metres per shadow texel in this cascade
		return mix(1.0, csm_pcf(suv, ndc.z - bias, atlas_texel, lights.dir_shadow_params.w, zscale, mpt), saturate(lights.dir_shadow_params.x));
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
	const float pad = 1.0 / 32.0;    // the atlas builder wraps the texture into this gutter: seamless up to the mip cap
	vec2 q = vec2(float(layer & 1), float(layer >> 1));
	return (q + pad + fract(uv) * (1.0 - 2.0 * pad)) * 0.5;
}

void clamp_grad(inout vec2 dx, inout vec2 dy)
{
	const float max_g = 1.0 / 32.0;   // never a mip whose gutter is under half a texel
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
	return 0.86 + 0.28 * n;
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
vec3 sample_env(vec3 R, float roughness, float mips)
{
	float lod = clamp(roughness, 0.0, 1.0) * max(mips - 1.0, 0.0);
	return decode_env(textureLod(u_env, env_uv(R), lod).rgb);
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
	float cam_dist = length(frame.camera_position - v_world_pos);
	float detail_w = clamp(1.0 - cam_dist / 70.0, 0.0, 1.0);
	// far: beyond ~250 m blend to the layer's mean colour (the 2x2 mip = one texel per quadrant) and a flat normal
	float far_w = clamp((cam_dist - 260.0) / 640.0, 0.0, 1.0);
	for (int i = 0; i < 4; ++i)
	{
		float wi = w[i];
		if (wi <= 0.002) continue;
		vec2 uv = p * tiles_per_metre[i];
		vec2 a = atlas_uv(uv, i);
		vec2 dx = dpx * tiles_per_metre[i] * 0.5, dy = dpy * tiles_per_metre[i] * 0.5;
		clamp_grad(dx, dy);
		// the same layer at a 4.3x larger, offset tiling blended in half: the two periods never line up (see terrain.hlsl)
		vec2 aM = atlas_uv(uv * 0.23 + vec2(0.21, 0.63), i);
		vec2 dxM = dx * 0.23, dyM = dy * 0.23;
		clamp_grad(dxM, dyM);
		if (obj.has_albedo_texture != 0u)
			albedo += wi * mix(srgb_to_linear(textureGrad(u_albedo, a, dx, dy).rgb), srgb_to_linear(textureGrad(u_albedo, aM, dxM, dyM).rgb), 0.5);
		else albedo += wi * vec3(0.5);
		if (obj.has_normal_texture != 0u)
			nm += wi * mix(decode_n(textureGrad(u_normal, a, dx, dy).rgb), decode_n(textureGrad(u_normal, aM, dxM, dyM).rgb), 0.5);
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
	if (far_w > 0.001 && obj.has_albedo_texture != 0u)
	{
		float amips = float(textureQueryLevels(u_albedo));
		vec3 mean = vec3(0.0);
		for (int k = 0; k < 4; ++k)
			if (w[k] > 0.002)
				mean += w[k] * srgb_to_linear(textureLod(u_albedo, vec2(float(k & 1) + 0.5, float(k >> 1) + 0.5) * 0.5, max(amips - 2.0, 0.0)).rgb);
		albedo = mix(albedo, mean, far_w);
		nm *= 1.0 - far_w;
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
	if (frame.env_sky.w > 1.5)
	{
		env_color = sample_env(R, roughness, frame.env_horizon.w) * frame.ambient_strength;   // the real sky, blurred by roughness
	}
	else if (frame.env_sky.w > 0.5)
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
		vec4 ss = texture(u_ssao, auv);
		ambient *= ss.a;
		if (frame.gi_strength > 0.0) ambient += ss.rgb * ss.rgb * 4.0 * albedo * (1.0 - metallic) * ss.a;   // screen-space GI (#257 step 1)
	}

	vec3 color = ambient + Lo;
	color = apply_fog(color, v_world_pos);

	color = tonemap_scene(color, frame.scene_exposure, frame.tonemapper);
	color = pow(max(color, 0.0), vec3(1.0 / 2.2));
	o_color = vec4(color, 1.0);
}

#endif
