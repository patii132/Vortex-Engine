#version 450
#extension GL_GOOGLE_include_directive : require
// Decal shading (decals.metal DecalPS): the scene position comes from the depth texture (texelFetch), pixels outside
// the unit box are rejected, the surface normal from the position derivatives drives the angle fade and the lighting.
#define SET_UNIFORM 3
#define VORTEX_NEED_DLIGHTS
#include "common.glsl"
#include "decals_common.glsl"

layout(location = 0) flat in vec4 v_inv0;
layout(location = 1) flat in vec4 v_inv1;
layout(location = 2) flat in vec4 v_inv2;
layout(location = 3) flat in vec4 v_inv3;
layout(location = 4) flat in vec3 v_axis;
layout(location = 5) flat in vec4 v_color;
layout(location = 6) flat in vec4 v_params;
layout(location = 0) out vec4 o_color;
layout(set = 2, binding = 0) uniform sampler2D u_tex;
layout(set = 2, binding = 1) uniform sampler2D u_depth;

vec3 world_from_depth(vec2 pix, float d)
{
	vec2 ndc = vec2(pix.x * f.screen.z * 2.0 - 1.0, 1.0 - pix.y * f.screen.w * 2.0);
	vec4 h = f.inv_view_projection * vec4(ndc, d, 1.0);
	return h.xyz / h.w;
}

void main()
{
	ivec2 dim = textureSize(u_depth, 0);
	ivec2 px = ivec2(clamp(gl_FragCoord.xy, vec2(0.0), vec2(dim) - 1.0));
	float d = texelFetch(u_depth, px, 0).r;
	if (d >= 0.99999) discard;   // sky
	vec3 wp = world_from_depth(gl_FragCoord.xy, d);
	vec3 lp = wp.x * v_inv0.xyz + wp.y * v_inv1.xyz + wp.z * v_inv2.xyz + v_inv3.xyz;
	if (any(greaterThan(abs(lp), vec3(0.5)))) discard;
	vec3 n = normalize(cross(dFdy(wp), dFdx(wp)));
	if (dot(n, f.eye.xyz - wp) < 0.0) n = -n;
	float facing = dot(n, v_axis);
	float fade = v_params.x > 0.001 ? saturate(facing / v_params.x) : (facing > 0.0 ? 1.0 : 0.0);
	fade *= 1.0 - smoothstep(0.35, 0.5, abs(lp.y));
	if (v_params.y > 0.0) fade *= saturate(1.0 - length(wp - f.eye.xyz) / v_params.y);
	vec2 uv = vec2(lp.x + 0.5, 0.5 - lp.z);
	vec4 t = b.has_texture != 0u ? texture(u_tex, uv) : vec4(1.0);
	vec4 c = t * b.base_color * v_color;
	c.a *= fade;
	if (c.a <= 0.002) discard;
	float fogf = fog_amount(wp);
	if (b.blend == 1u) { o_color = vec4(mix(vec3(1.0), c.rgb, c.a * (1.0 - fogf)), 1.0); return; }
	if (b.blend == 2u) { o_color = vec4(c.rgb * c.a * (1.0 - fogf), 1.0); return; }
	vec3 lin = pow(max(c.rgb, vec3(0.0)), vec3(2.2)) * decal_light(wp, n);
	vec3 rgb = pow(tonemap_scene(lin, f.fog2.z, f.counts.z), vec3(1.0 / 2.2));
	rgb = mix(rgb, f.fog.rgb, fogf);
	o_color = vec4(rgb, c.a);
}
