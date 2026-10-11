// Shared helpers for the Vulkan/SPIR-V twins of Engine/Shaders/msl/*.metal.
//
// The GLSL set is a 1:1 port of the MSL set: same math, same uniform byte layouts, same binding slots.
// SDL GPU normalises the coordinate system across backends ("SDL will automatically convert the coordinate
// system behind the scenes" — SDL_gpu.h), so no clip-space Y flip and no winding change is needed here;
// every vertex shader emits exactly the clip position its .metal twin emits.
//
// Resource sets follow SDL GPU's SPIR-V rules (SDL_CreateGPUShader):
//   vertex   — set 0: sampled textures, then storage textures, then storage buffers; set 1: uniform buffers
//   fragment — set 2: sampled textures, then storage textures, then storage buffers; set 3: uniform buffers
//
// std140 note: MSL's tightly packed `float pad[N]` is NOT layout-compatible with std140 (16-byte array
// stride), so every scalar array in a uniform block is spelled out as individual scalars below.

#ifndef VORTEX_COMMON_GLSL
#define VORTEX_COMMON_GLSL

#define PI 3.14159265359

float  saturate(float x) { return clamp(x, 0.0, 1.0); }
vec2   saturate(vec2  x) { return clamp(x, vec2(0.0), vec2(1.0)); }
vec3   saturate(vec3  x) { return clamp(x, vec3(0.0), vec3(1.0)); }
vec4   saturate(vec4  x) { return clamp(x, vec4(0.0), vec4(1.0)); }

// ACES filmic (RRT+ODT fit) — byte-identical math to the .metal twins.
// Khronos PBR Neutral: hue-preserving with a gentle highlight desaturation (the scene's tonemapper 1). Input = the
// EXPOSED scene colour.
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

// The scene's tonemapper after the scene exposure: 0 = the ACES fit (classic), 1 = PBR Neutral.
vec3 tonemap_scene(vec3 color, float exposure, uint mode)
{
	vec3 x = color * (exposure > 0.0 ? exposure : 0.5);
	if (mode == 1u) return saturate(tonemap_neutral(max(x, 0.0)));
	vec3 a = x * (x + 0.0245786) - 0.000090537;
	vec3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
	return saturate(a / b);
}

vec3 aces_tonemap_exposed(vec3 color, float exposure)
{
	vec3 x = color * (exposure > 0.0 ? exposure : 0.5);   // the scene exposure (camera aperture)
	vec3 a = x * (x + 0.0245786) - 0.000090537;
	vec3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
	return saturate(a / b);
}

vec3 aces_tonemap(vec3 color)
{
	vec3 x = color * 0.5;
	vec3 a = x * (x + 0.0245786) - 0.000090537;
	vec3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
	return saturate(a / b);
}

#endif
