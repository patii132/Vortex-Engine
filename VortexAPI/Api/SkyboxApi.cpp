#include "../ApiCommon.h"

EDITOR_INTERFACE void SetSkyboxEnabled(bool enabled)
{
	graphics::Renderer::instance().set_skybox_enabled(enabled);
}

EDITOR_INTERFACE bool IsSkyboxEnabled()
{
	return graphics::Renderer::instance().is_skybox_enabled();
}

EDITOR_INTERFACE void SetSkyboxMode(unsigned int mode)
{
	graphics::Renderer::instance().set_skybox_mode(
		static_cast<graphics::Renderer::SkyboxMode>(mode));
}

EDITOR_INTERFACE unsigned int GetSkyboxMode()
{
	return static_cast<unsigned int>(graphics::Renderer::instance().get_skybox_mode());
}

EDITOR_INTERFACE void SetSkyboxColors(
	float skyR, float skyG, float skyB,
	float horizonR, float horizonG, float horizonB,
	float groundR, float groundG, float groundB)
{
	graphics::Renderer::instance().set_skybox_colors(
		{ skyR, skyG, skyB },
		{ horizonR, horizonG, horizonB },
		{ groundR, groundG, groundB }
	);
}

EDITOR_INTERFACE void SetSkyboxSolidColor(float r, float g, float b)
{
	graphics::Renderer::instance().set_skybox_solid_color({ r, g, b });
}

EDITOR_INTERFACE void SetSkyboxSun(float dirX, float dirY, float dirZ, float colorR, float colorG, float colorB, float intensity)
{
	graphics::Renderer::instance().set_skybox_sun(
		{ dirX, dirY, dirZ },
		{ colorR, colorG, colorB },
		intensity);
}

// Equirect texture sky (#326): the registry texture the fullscreen sky pass samples in SkyboxMode::Texture, its
// exposure and a yaw offset in degrees. Drawn behind everything, centred on whichever camera renders the frame.
EDITOR_INTERFACE void SetSkyboxTexture(id::id_type texture_id, float exposure, float rotation_degrees)
{
	graphics::Renderer::instance().set_skybox_texture(texture_id, exposure, rotation_degrees * 0.01745329252f);
}

// The same with an HDR mode: 0 = an 8-bit picture shown as it is; 1 = a float picture (.hdr) shown the classic way (gamma-
// encoded, clamped at white — what the 8-bit import used to give); 2 = the float picture run through the scene exposure +
// the tonemapper by the sky pass, so the sky and the lit world share one exposure.
EDITOR_INTERFACE void SetSkyboxTextureHdr(id::id_type texture_id, float exposure, float rotation_degrees, int32_t hdr)
{
	graphics::Renderer::instance().set_skybox_texture(texture_id, exposure, rotation_degrees * 0.01745329252f, hdr < 0 ? 0 : (hdr > 2 ? 2 : hdr));
}

// Sky light (image-based lighting, step 1): SH9 irradiance per channel (27 floats, coefficient-major) baked by the
// managed SkyLight from the sky's HDR or gradient; enabled = 0 returns the shaders to their neutral hemisphere ambient.
EDITOR_INTERFACE void SetSkyLight(const float* sh27, int enabled)
{
	graphics::Renderer::instance().set_sky_light(sh27, enabled != 0);
}

// The sky as a REFLECTION (IBL step 2): a registry texture holding a linear equirect of the sky with a mip chain; the
// PBR shaders sample it by roughness for their environment specular, the water for its sky. invalid id = off.
EDITOR_INTERFACE void SetEnvironmentMap(id::id_type texture_id, int mip_count)
{
	graphics::Renderer::instance().set_environment_map(texture_id, mip_count);
}

// ============== SKYBOX COMPONENT API (Runtime) ==============

namespace {
	struct skybox_descriptor {
		u8 mode; // 0 = solid, 1 = gradient, 2 = cubemap
		f32 sky_color[3];
		f32 horizon_color[3];
		f32 ground_color[3];
		f32 sun_direction[3];
		f32 sun_color[3];
		f32 sun_intensity;
		f32 ambient_intensity;
		f32 exposure;
		bool is_enabled;
	};
}

EDITOR_INTERFACE id::id_type CreateSkyboxComponent(skybox_descriptor* desc)
{
	if (!desc) return id::invalid_id;
	
	skybox::init_info info{};
	info.mode = static_cast<skybox::skybox_mode>(desc->mode);
	memcpy(info.sky_color, desc->sky_color, sizeof(f32) * 3);
	memcpy(info.horizon_color, desc->horizon_color, sizeof(f32) * 3);
	memcpy(info.ground_color, desc->ground_color, sizeof(f32) * 3);
	memcpy(info.sun_direction, desc->sun_direction, sizeof(f32) * 3);
	memcpy(info.sun_color, desc->sun_color, sizeof(f32) * 3);
	info.sun_intensity = desc->sun_intensity;
	info.ambient_intensity = desc->ambient_intensity;
	info.exposure = desc->exposure;
	info.is_enabled = desc->is_enabled;
	
	return skybox::create(info).get_id();
}

EDITOR_INTERFACE void RemoveSkyboxComponent(id::id_type skybox_id)
{
	skybox::remove(skybox::component{ skybox::skybox_id{skybox_id} });
}

EDITOR_INTERFACE void ApplySkyboxToRenderer(id::id_type skybox_id)
{
	skybox::component skybox{ skybox::skybox_id{skybox_id} };
	if (skybox.is_valid())
	{
		skybox.apply_to_renderer();
	}
}

EDITOR_INTERFACE void ApplyActiveSkybox()
{
	auto skybox = skybox::get_active_skybox();
	if (skybox.is_valid())
	{
		skybox.apply_to_renderer();
	}
}

EDITOR_INTERFACE void SetActiveSkyboxComponent(id::id_type skybox_id)
{
	skybox::set_active_skybox(skybox::component{ skybox::skybox_id{skybox_id} });
}
