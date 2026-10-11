#version 450
#extension GL_GOOGLE_include_directive : require
// Standard PBR fragment stage — the Vulkan twin of standard.metal PSMain.
// Cook-Torrance GGX + directional/point/spot lights + hemisphere ambient + environment/rim + fog + ACES.
//
// Texture slots match the MSL twin and the C++ binding array (SdlGpuRenderer_Scene.cpp):
//   0 albedo  1 normal  2 metallic  3 roughness  4 ao  5 height
//   6 spot shadow atlas  7 cascade (CSM) atlas  8 point shadow atlas   (comparison samplers -> sampler2DShadow)
//   9 ssao
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

// ---------------------------------------------------------------- BRDF
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

// Height fog (#328): density is uniform up to fog_height_y and falls off as exp(-k * (y - fog_height_y)) above
// it, integrated along the camera ray (the camera's own height counts, nothing is clamped to a band).
float fog_optical_depth(float density, float height_y, float k, vec3 cam, vec3 world_pos)
{
	vec3 delta = world_pos - cam;
	float dist = length(delta);
	if (k <= 0.0) { float d = density * dist; return -log2(max(exp2(-d * d), 1e-6)); }   // uniform fog, the old exp2 look
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

// ---- shadow sampling (byte-matched to standard.hlsl / standard.metal: spot atlas 2x2 tiles, cascade atlas
//      2x2 tiles, point atlas 4x3 tiles of 1024²). sampler2DShadow = hardware PCF tap. 1 = lit. ----
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
float csm_pcf(vec2 suv, float depth, float atlas_texel, float softness)
{
	float lit = 0.0;
	if (softness <= 1.01)
	{
		for (int y = -1; y <= 1; ++y)
			for (int x = -1; x <= 1; ++x)
				lit += texture(u_csm_shadow, vec3(suv + vec2(float(x), float(y)) * atlas_texel, depth));
		return lit / 9.0;
	}
	float r = softness * atlas_texel;
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
		return mix(1.0, csm_pcf(suv, ndc.z - bias, atlas_texel, lights.dir_shadow_params.w), saturate(lights.dir_shadow_params.x));
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

// Packed PBR maps: PerObject::has_*_texture is 1 + the channel to read (1 R, 2 G, 3 B, 4 A) — glTF / ORM maps
// keep roughness in G, metallic in B and occlusion in R.
float pick_channel(vec4 v, uint flag)
{
	return flag == 2u ? v.g : (flag == 3u ? v.b : (flag == 4u ? v.a : v.r));
}

// Per-pixel tangent frame from screen-space derivatives (Schueler, "Normal Mapping Without Precomputed
// Tangents"): meshes carry no tangents, and a frame derived from the normal alone ignores the UV layout, so
// normal/parallax maps on arbitrary UV islands were lit from the wrong side. T follows +u, B follows +v
// (image-down: the importer flips V), so DirectX-convention normal maps apply as-is and OpenGL ones flip
// green. The determinant's sign keeps it independent of the screen's y axis and of mirrored UVs. Leaves T/B
// untouched where the UVs have no gradient.
void cotangent_frame(vec3 N, vec3 p, vec2 uv, inout vec3 T, inout vec3 B)
{
	vec3 dp1 = dFdx(p), dp2 = dFdy(p);
	vec2 duv1 = dFdx(uv), duv2 = dFdy(uv);
	vec3 dp2perp = cross(dp2, N), dp1perp = cross(N, dp1);
	vec3 t = dp2perp * duv1.x + dp1perp * duv2.x;
	vec3 b = dp2perp * duv1.y + dp1perp * duv2.y;
	float det = dot(dp1, dp2perp);
	float m = max(dot(t, t), dot(b, b));
	if (m < 1e-30 || abs(det) < 1e-30) return;
	float k = inversesqrt(m) * (det < 0.0 ? -1.0 : 1.0);
	T = t * k;
	B = b * k;
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
	vec2 tiling = (obj.uv_tiling.x > 0.0 && obj.uv_tiling.y > 0.0) ? obj.uv_tiling : vec2(1.0, 1.0);
	vec2 uv = v_uv * tiling;

	vec3 cam_pos = frame.camera_position;

	vec3 Ng = normalize(v_norm);
	vec3 T = normalize(v_tangent), B = normalize(v_bitangent);
	cotangent_frame(Ng, v_world_pos, uv, T, B);   // outside any branch: it needs derivatives

	if (obj.has_height_texture != 0u && obj.height_scale > 0.0)
	{
		vec3 Vw = normalize(cam_pos - v_world_pos);
		mat3 TBN = mat3(normalize(T), normalize(B), Ng);
		// HLSL mul(TBN, V) with TBN rows = T,B,N  ->  dot each row with V
		vec3 Vt = vec3(dot(TBN[0], Vw), dot(TBN[1], Vw), dot(TBN[2], Vw));
		float h = texture(u_height, uv).r;
		uv -= (Vt.xy / max(Vt.z, 0.15)) * ((1.0 - h) * obj.height_scale);
	}

	vec3 albedo = obj.base_color.rgb * v_tint.rgb;   // the instance tint multiplies the base colour (#331)
	float alpha = obj.base_color.a * v_tint.a;
	if (obj.has_albedo_texture != 0u)
	{
		vec4 tex = texture(u_albedo, uv);
		albedo *= srgb_to_linear(tex.rgb);   // base colour TINTS the texture, alpha multiplies — standard PBR (#330)
		alpha *= tex.a;
	}
	// AlphaTest (#329): cut-outs (foliage, fences, hair cards) drop their transparent texels here
	if (obj.alpha_cutoff > 0.0 && alpha < obj.alpha_cutoff) discard;

	if (obj.is_unlit != 0u)
	{
		vec3 emissive = albedo * obj.emissive_strength;
		emissive = apply_fog(emissive, v_world_pos);
		emissive = emissive / (emissive + 1.0);
		emissive = pow(emissive, vec3(1.0 / 2.2));
		o_color = vec4(emissive, alpha);
		return;
	}

	float metallic = obj.metallic;
	if (obj.has_metallic_texture != 0u) metallic = pick_channel(texture(u_metallic, uv), obj.has_metallic_texture);

	float roughness = max(obj.roughness, 0.04);
	if (obj.has_roughness_texture != 0u) roughness = max(pick_channel(texture(u_roughness, uv), obj.has_roughness_texture), 0.04);

	float ao = obj.ao;
	if (obj.has_ao_texture != 0u) ao = pick_channel(texture(u_ao, uv), obj.has_ao_texture);

	vec3 N = Ng;
	if (obj.has_normal_texture != 0u)
	{
		vec3 nm = texture(u_normal, uv).rgb * 2.0 - 1.0;
		if (obj.use_directx_normals == 0u) nm.y = -nm.y;
		nm.xy *= obj.normal_strength;
		N = normalize(nm.x * T + nm.y * B + nm.z * N);
	}

	vec3 V = normalize(cam_pos - v_world_pos);
	float NdotV = max(dot(N, V), 0.001);
	vec3 F0 = mix(vec3(0.04), albedo, metallic);
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

	for (uint i = 0u; i < frame.point_light_count && i < uint(MAX_POINT_LIGHTS); ++i)
	{
		vec3 lpos = lights.point_lights[i].position;
		vec3 light_vec = lpos - v_world_pos;
		float dist = length(light_vec);
		if (dist < lights.point_lights[i].range)
		{
			vec3 L = light_vec / dist;
			vec3 H = normalize(V + L);
			float NdotL = max(dot(N, L), 0.0);
			float NdotH = max(dot(N, H), 0.0);
			float VdotH = max(dot(V, H), 0.0);
			float atten = attenuation(dist, lights.point_lights[i].range);
			atten *= sample_point_shadow(v_world_pos, lpos, int(i));
			vec3 radiance = lights.point_lights[i].color * lights.point_lights[i].intensity * atten;
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
					atten *= sample_spot_shadow(v_world_pos,
						int(lights.spot_lights[j].shadow_slot),
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
	vec3 ambient = hemisphere * frame.ambient_strength * albedo * ao * (1.0 - metallic);
	if (frame.ssao_enabled > 0.5)
	{
		vec2 auv = gl_FragCoord.xy * vec2(frame.ssao_padding0, frame.ssao_padding1);
		ambient *= texture(u_ssao, auv).r;
	}

	float rim_fresnel = pow(saturate(1.0 - NdotV), 5.0);
	vec3 rim_light = rim_fresnel * F0 * 0.1 * ao * metallic;

	vec3 R = reflect(-V, N);
	vec3 env_color;
	if (frame.env_sky.w > 1.5)
	{
		env_color = sample_env(R, roughness, frame.env_horizon.w) * frame.ambient_strength;   // the real sky, blurred by roughness
	}
	else if (frame.env_sky.w > 0.5)
	{
		// Reflections of the scene's own sky gradient (zenith / horizon / ground), blurred toward its average
		// with roughness — metals (weapons!) pick up the sky instead of turning black. Scaled like the
		// diffuse ambient.
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
	vec3 specular_ambient = env_color * env_fresnel * ao;
	ambient += specular_ambient + rim_light;

	vec3 color = ambient + Lo;
	color = apply_fog(color, v_world_pos);

	color = tonemap_scene(color, frame.scene_exposure, frame.tonemapper);
	color = pow(max(color, 0.0), vec3(1.0 / 2.2));
	o_color = vec4(color, alpha);
}
