// Procedural gradient skybox constants (skybox.metal). Each packed_float3 + float pair is one 16-byte
// std140 slot, matching the MSL layout exactly.
#ifndef VORTEX_SKYBOX_COMMON_GLSL
#define VORTEX_SKYBOX_COMMON_GLSL

layout(set = SET_UNIFORM, binding = 0, std140) uniform SkyboxConstants
{
	mat4  inverse_view_projection;  // @0
	vec3  camera_position; float padding0;      // @64
	vec3  sky_color;       float padding1;      // @80
	vec3  horizon_color;   float padding2;      // @96
	vec3  ground_color;    float padding3;      // @112
	vec3  sun_direction;   float sun_intensity; // @128
	vec3  sun_color;       float tonemapper;    // @144  the HDR path's curve: 0 = ACES fit, 1 = PBR Neutral
	vec4  params;                               // @160 x: 1 = sample the equirect texture (#326), y: exposure, z: yaw offset
} c;

#endif
