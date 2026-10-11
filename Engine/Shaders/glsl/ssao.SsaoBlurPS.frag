#version 450
// SSAO 4-tap blur (ssao.metal SsaoBlurPS).
layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_src;

layout(set = 3, binding = 0, std140) uniform SsaoCB
{
	mat4  inv_proj;
	vec2  texel;
	float radius;
	float intensity;
	float bias;
	float proj_scale;
	vec2  pad;
	mat4  proj;              // @96  view -> clip (screen-space GI, #257 step 1)
	mat4  view_to_prev_clip; // @160 view -> the previous frame's clip
	float gi_strength;       // @224
	float scene_exposure;
	float prev_valid;
	float gi_radius;
} c;

void main()
{
	vec4 s = vec4(0.0);
	s += texture(u_src, v_uv + c.texel * vec2(-0.5, -0.5));
	s += texture(u_src, v_uv + c.texel * vec2( 1.5, -0.5));
	s += texture(u_src, v_uv + c.texel * vec2(-0.5,  1.5));
	s += texture(u_src, v_uv + c.texel * vec2( 1.5,  1.5));
	o_color = s * 0.25;   // rgb = GI, a = AO
}
