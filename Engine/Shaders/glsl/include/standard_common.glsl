// Standard PBR uniform blocks — the Vulkan twin of standard.metal's PerFrame / PerObject / LightBuffer.
//
// Byte-matched to the C++ PerFrameConstants (240) / PerObjectConstants (144) / LightBufferData (2304) in
// SdlGpuRenderer.h, i.e. the same ABI the DX12 cbuffers use. Two std140 rules drive the spelling:
//   * a `vec3` takes a 16-byte slot but leaves its last 4 bytes to a following `float` — exactly how MSL
//     packs `packed_float3` + `float`, so those pairs port verbatim;
//   * a scalar array has a 16-byte stride in std140, so MSL's tightly packed `uint pad[3]` / `float pad[3]`
//     must be spelled out as individual scalars. ssao_padding0/1 are not padding: they carry the AO UV scale.
#ifndef VORTEX_STANDARD_COMMON_GLSL
#define VORTEX_STANDARD_COMMON_GLSL

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8

layout(set = SET_UNIFORM, binding = 0, std140) uniform PerFrameBlock
{
	mat4  view_projection;        // @0
	vec3  camera_position;        // @64
	float padding0;               // @76
	vec3  light_direction;        // @80
	float directional_intensity;  // @92
	vec3  light_color;            // @96
	float ambient_strength;       // @108
	uint  point_light_count;      // @112
	uint  spot_light_count;       // @116
	uint  frame_padding0;         // @120
	uint  frame_padding1;         // @124
	vec3  fog_color;              // @128
	float fog_density;            // @140
	float fog_height_y;           // @144
	float fog_height_falloff;     // @148
	uint  fog_mode;               // @152
	float scene_exposure;         // @156  pre-tonemap multiplier (0.5 = classic; <= 0 reads as 0.5)
	float shadow_map_texel;       // @160
	uint  tonemapper;             // @164  0 = ACES fit, 1 = PBR Neutral
	uint  shadow_padding1;        // @168
	uint  shadow_padding2;        // @172
	float ssao_enabled;           // @176
	float ssao_padding0;          // @180  AO UV scale x
	float ssao_padding1;          // @184  AO UV scale y
	float fog_sun_glow;           // @188  the sun's glow in the fog (0 = off)
	vec4  env_sky;                // @192  scene sky gradient for reflections (rgb); w = 1 when a sky is set
	vec4  env_horizon;            // @208
	vec4  env_ground;             // @224
	vec4  camera_clip;            // @240  x near, y far clip of this view (#200 refraction / SSR)
} frame;                          // = 256

#ifdef VORTEX_NEED_PER_OBJECT
layout(set = SET_UNIFORM, binding = 1, std140) uniform PerObjectBlock
{
	mat4  world;                  // @0 (unused by the VS: world comes per instance)
	vec4  base_color;             // @64
	float metallic;               // @80
	float roughness;              // @84
	float ao;                     // @88
	float normal_strength;        // @92
	uint  has_albedo_texture;     // @96
	uint  has_normal_texture;     // @100
	uint  has_metallic_texture;   // @104
	uint  has_roughness_texture;  // @108
	uint  has_ao_texture;         // @112
	uint  use_directx_normals;    // @116
	uint  is_unlit;               // @120
	float emissive_strength;      // @124
	vec2  uv_tiling;              // @128
	uint  has_height_texture;     // @136
	float height_scale;           // @140
	float alpha_cutoff;           // @144 (#329: > 0 = AlphaTest cutoff)
	float _pad0; float _pad1; float _pad2;
} obj;                            // = 160
#endif

#ifdef VORTEX_NEED_LIGHTS
struct PointLight
{
	vec3  position; float range;
	vec3  color;    float intensity;
};                                // = 32

struct SpotLight
{
	vec3  position;  float range;
	vec3  direction; float spot_angle;
	vec3  color;     float intensity;
	float inner_spot_angle;
	float shadow_strength;
	float shadow_bias;
	float shadow_slot;
};                                // = 64

layout(set = SET_UNIFORM, binding = 2, std140) uniform LightBufferBlock
{
	PointLight point_lights[MAX_POINT_LIGHTS];  // @0
	SpotLight  spot_lights[MAX_SPOT_LIGHTS];    // @512
	mat4       shadow_vp[4];                    // @1024
	mat4       cascade_vp[3];                   // @1280
	vec4       cascade_splits;                  // @1472
	vec4       dir_shadow_params;               // @1488
	vec4       point_shadows[2];                // @1504
	mat4       point_face_vp[12];               // @1536
	vec4       sky_sh[9];                       // @2304 .. 2448  sky light: SH9 irradiance per channel, w of [0] = on
} lights;                                       // = 2448
#endif

// Vertex-stage helper shared by VSMain and VSSkinned: a stable frame from the normal alone (the fragment
// stage refines it from screen-space derivatives in cotangent_frame).
void tangent_basis(vec3 N, out vec3 T, out vec3 B)
{
	vec3 c = cross(N, vec3(0.0, 1.0, 0.0));
	T = (dot(c, c) < 1e-6) ? normalize(cross(N, vec3(1.0, 0.0, 0.0))) : normalize(c);
	B = normalize(cross(N, T));
}

#endif
