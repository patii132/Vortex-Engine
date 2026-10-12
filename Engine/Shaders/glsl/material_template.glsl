// Custom material shader template (Vulkan / SPIR-V backend) — the GLSL twin of starting from standard.metal.
//
// ONE file provides both stages: the engine compiles it twice with glslc, once with VORTEX_VERTEX_STAGE and
// once with VORTEX_FRAGMENT_STAGE defined (SdlGpuRenderer::load_material_shader), because a SPIR-V module
// carries a single `main`. Saving the file re-compiles it — alt-tab back to the editor and the change is in.
//
// The bindings are the engine's standard material layout, so `frame`, `obj`, `lights` and all ten texture
// slots are already available; "standard_common.glsl" and "standard_vsout.glsl" ship next to this file.
#version 450
#extension GL_GOOGLE_include_directive : require

// ---------------------------------------------------------------------------------- vertex stage
#ifdef VORTEX_VERTEX_STAGE
#define SET_UNIFORM 1
#include "standard_common.glsl"
#include "standard_vsout.glsl"

layout(location = 0) in vec3 a_pos;
layout(location = 1) in vec3 a_norm;
layout(location = 2) in vec2 a_uv;
layout(location = 3) in vec4 a_iw0;   // per-instance world matrix columns
layout(location = 4) in vec4 a_iw1;
layout(location = 5) in vec4 a_iw2;
layout(location = 6) in vec4 a_iw3;

void main()
{
	mat4 world = mat4(a_iw0, a_iw1, a_iw2, a_iw3);
	vec4 world_pos = world * vec4(a_pos, 1.0);
	v_world_pos = world_pos.xyz;
	gl_Position = frame.view_projection * world_pos;
	mat3 world3 = mat3(a_iw0.xyz, a_iw1.xyz, a_iw2.xyz);
	v_norm = normalize(world3 * a_norm);
	v_uv = a_uv;
	tangent_basis(v_norm, v_tangent, v_bitangent);
}
#endif

// ---------------------------------------------------------------------------------- fragment stage
#ifdef VORTEX_FRAGMENT_STAGE
#define SET_UNIFORM 3
#define VORTEX_NEED_PER_OBJECT
#define VORTEX_NEED_LIGHTS
#define VORTEX_VSOUT_IN
#include "common.glsl"
#include "standard_common.glsl"
#include "standard_vsout.glsl"

layout(location = 0) out vec4 o_color;

// Slots 0..9: albedo, normal, metallic, roughness, ao, height, spot/CSM/point shadows, ssao.
layout(set = 2, binding = 0) uniform sampler2D u_albedo;

void main()
{
	// Edit from here. A plain lambert term over the material's albedo, so the template renders something
	// recognisable before you change it.
	vec3 albedo = obj.base_color.rgb;
	if (obj.has_albedo_texture != 0u) albedo *= pow(max(texture(u_albedo, v_uv).rgb, 0.0), vec3(2.2));   // tinted by base colour (#330)

	vec3 N = normalize(v_norm);
	vec3 L = normalize(-frame.light_direction);
	float ndotl = max(dot(N, L), 0.0);
	vec3 color = albedo * (frame.light_color * frame.directional_intensity * ndotl + frame.ambient_strength);

	color = tonemap_scene(color, frame.scene_exposure, frame.tonemapper);
	o_color = vec4(pow(max(color, 0.0), vec3(1.0 / 2.2)), obj.base_color.a);
}
#endif
