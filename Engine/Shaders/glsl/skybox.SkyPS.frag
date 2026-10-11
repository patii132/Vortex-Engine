#version 450
#extension GL_GOOGLE_include_directive : require
// Sky/horizon/ground gradient + sun disc + glow (skybox.metal SkyPS).
#define SET_UNIFORM 3
#include "common.glsl"
#include "skybox_common.glsl"

layout(set = 2, binding = 0) uniform sampler2D u_sky;   // the equirect sky (#326), white when unused

layout(location = 0) in  vec3 v_world_dir;
layout(location = 0) out vec4 o_color;

void main()
{
	vec3 dir = normalize(v_world_dir);
	float y = dir.y;

	// Equirect texture sky (#326): sampled at the far plane, no depth write, centred on the rendering camera.
	if (c.params.x > 0.5)
	{
		const float two_pi = 6.28318530718;
		float u = atan(dir.x, dir.z) / two_pi + 0.5 + c.params.z / two_pi;
		float v = acos(clamp(y, -1.0, 1.0)) / 3.14159265359;
		vec3 sky = textureLod(u_sky, vec2(u, v), 0.0).rgb * c.params.y;
		if (c.params.w > 0.0 && c.haze.w > 0.0)
		{
			// aerial perspective on the sky: the horizon whitens into the scene's haze, the sun glows in it, the zenith keeps its blue
			float h = max(y, 0.0);
			float f = 1.0 - exp(-c.haze.w * 40.0 / (h + 0.04));
			float mu = saturate(dot(dir, normalize(-c.glow_dir.xyz)));
			vec3 hz = c.haze.rgb + c.glow_color.rgb * (c.glow_dir.w * (0.12 * mu * mu + 0.88 * pow(mu, 10.0)));
			sky = mix(sky, hz, f);
		}
		if (c.params.w < 0.0) sky = pow(saturate(sky), vec3(1.0 / 2.2));   // a float picture the classic way: gamma-encoded, clamped
		else if (c.params.w > 0.0)
		{
			// an HDR picture: the scene exposure + ACES + gamma of the lit surfaces, so the sky and the world agree
			sky = pow(tonemap_scene(sky, c.params.w, c.tonemapper > 0.5 ? 1u : 0u), vec3(1.0 / 2.2));
		}
		o_color = vec4(sky, 1.0);
		return;
	}
	vec3 color;
	if (y > 0.0)
	{
		float t = pow(y, 0.4);
		color = mix(c.horizon_color, c.sky_color, t);
	}
	else
	{
		float t = pow(-y, 0.7);
		color = mix(c.horizon_color, c.ground_color, t);
	}

	if (c.sun_intensity > 0.001)
	{
		vec3 sun_dir = normalize(-c.sun_direction);
		float sun_dot = dot(dir, sun_dir);
		float sun_disc = smoothstep(0.9995, 0.9999, sun_dot);
		color += c.sun_color * sun_disc * c.sun_intensity * 10.0;
		float sun_glow = pow(max(sun_dot, 0.0), 256.0);
		color += c.sun_color * sun_glow * c.sun_intensity * 0.5;
		float horizon_glow = pow(1.0 - abs(y), 4.0) * pow(max(sun_dot, 0.0), 2.0);
		color += c.sun_color * horizon_glow * c.sun_intensity * 0.3;
	}

	float noise = fract(sin(dot(dir.xz, vec2(12.9898, 78.233))) * 43758.5453);
	color += (noise - 0.5) * 0.01;
	o_color = vec4(color, 1.0);
}
