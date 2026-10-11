#version 450
#extension GL_GOOGLE_include_directive : require
// Particle shading (particles.metal ParticlePS). Drawn in a pass WITHOUT a depth attachment: the scene depth
// is sampled instead, which gives both the depth test (discard) and soft particles (fade near geometry).
// The depth texture is read with texelFetch (MSL's depth.read), so its sampler is never used for filtering.
#define SET_UNIFORM 3
#include "common.glsl"
#include "particles_common.glsl"
#define VORTEX_PVOUT_IN
#include "particles_pvout.glsl"

layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_tex;
layout(set = 2, binding = 1) uniform sampler2D u_depth;

float linear_depth(float d)
{
	float n = f.depth_params.x, fa = f.depth_params.y;
	if (f.depth_params.z > 0.5) return n + d * (fa - n);
	return (n * fa) / max(fa - d * (fa - n), 1e-6);
}

void main()
{
	vec4 t;
	if (b.has_texture != 0u)
	{
		t = texture(u_tex, v_uv0);
		if (b.frame_blend != 0u) t = mix(t, texture(u_tex, v_uv1), v_blend_t);
	}
	else
	{
		// no texture: a soft round puff (billboards) / a soft-edged strip (ribbons)
		float r = b.mode == MODE_RIBBON ? abs(v_local.y) : length(v_local);
		float a = saturate(1.0 - r);
		a = a * a * (3.0 - 2.0 * a);
		t = vec4(1.0, 1.0, 1.0, a);
	}
	vec4 c = t * v_color;
	if (b.blend == 2u) c.rgb *= v_color.a;   // premultiplied: the tint's alpha scales the colour too

	// depth test + soft particles against this pass's scene depth (no depth attachment is bound)
	ivec2 dim = textureSize(u_depth, 0);
	uvec2 px = uvec2(max(gl_FragCoord.xy, vec2(0.0)));
	px = min(px, uvec2(uint(dim.x) - 1u, uint(dim.y) - 1u));
	float scene_z = linear_depth(texelFetch(u_depth, ivec2(px), 0).r);
	float dz = scene_z - v_view_z;
	if (dz < 0.0) discard;
	float fade = b.soft_inv > 0.0 ? saturate(dz * b.soft_inv) : 1.0;
	fade *= saturate((v_view_z - f.depth_params.x) * 5.0);   // no pop at the near plane

	if (b.lit != 0u)
	{
		vec3 lin = pow(max(c.rgb, 0.0), vec3(2.2)) * v_light;
		c.rgb = pow(tonemap_scene(lin, f.fog2.z, f.counts.z), vec3(1.0 / 2.2));
	}
	c.rgb *= b.emissive;
	if (b.blend == 1u) c.rgb *= (1.0 - v_fog);                        // additive glows vanish into the fog
	else if (b.blend == 2u) c.rgb = mix(c.rgb, f.fog.rgb * c.a, v_fog);
	else c.rgb = mix(c.rgb, f.fog.rgb, v_fog);
	c.a *= fade;
	if (b.blend == 2u) c.rgb *= fade;
	if (c.a <= 0.001 && b.blend != 2u) discard;
	o_color = c;
}
