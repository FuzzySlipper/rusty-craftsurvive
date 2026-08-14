use rusty_engine::render_model::{
    RenderDiff, SkyBackgroundDescriptor, TextureDescriptor, TextureFilter, TexturePayloadSource,
    TextureWrap,
};

const SKY_BYTES: &[u8] = include_bytes!("../content/textures/sky-panorama.png");
pub const SKY_URL: &str = "/assets/sky-panorama.png";

const SKY_TEXTURE_ID: &str = "texture/craftsurvive-sky-panorama";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SkyBackgroundResource {
    pub identity: String,
    pub content_hash: String,
    pub media_type: &'static str,
    pub url: &'static str,
    pub bytes: &'static [u8],
}

pub fn sky_background_resource() -> Result<SkyBackgroundResource, String> {
    let texture = sky_texture_descriptor()?;
    let payload = texture
        .payload
        .as_ref()
        .ok_or_else(|| "sky texture descriptor has no retained payload".to_owned())?;
    let TexturePayloadSource::Resource { resource } = &payload.source else {
        return Err("sky texture must use a retained resource".to_owned());
    };
    Ok(SkyBackgroundResource {
        identity: resource.clone(),
        content_hash: payload.content_hash.clone(),
        media_type: "image/png",
        url: SKY_URL,
        bytes: SKY_BYTES,
    })
}

pub fn sky_background_ops() -> Result<Vec<RenderDiff>, String> {
    let texture = sky_texture_descriptor()?;
    let background = SkyBackgroundDescriptor {
        texture: texture.id.clone(),
    };
    Ok(vec![
        RenderDiff::DefineTexture { texture },
        RenderDiff::SetSkyBackground {
            background: Some(background),
        },
    ])
}

fn sky_texture_descriptor() -> Result<TextureDescriptor, String> {
    TextureDescriptor::admit_png_rgba8_resource(
        SKY_TEXTURE_ID.to_owned(),
        SKY_BYTES,
        TextureFilter::Linear,
        TextureWrap::Clamp,
        1,
    )
    .map_err(|error| format!("admit sky panorama texture: {error:?}"))
}

#[cfg(test)]
mod tests {
    use rusty_engine::render_model::TextureColorSpace;

    use super::*;

    #[test]
    fn panorama_is_an_exact_authored_sky_resource() {
        let resource = sky_background_resource().unwrap();
        assert_eq!(resource.bytes, SKY_BYTES);
        assert_eq!(resource.media_type, "image/png");
        assert!(resource.identity.starts_with("texture-resource/"));

        let operations = sky_background_ops().unwrap();
        let RenderDiff::DefineTexture { texture } = &operations[0] else {
            panic!("sky texture must be defined before selection");
        };
        assert_eq!(texture.width, texture.height * 2);
        assert_eq!(texture.filter, TextureFilter::Linear);
        assert_eq!(texture.wrap, TextureWrap::Clamp);
        assert_eq!(
            texture.payload.as_ref().unwrap().color_space,
            TextureColorSpace::Srgb
        );
        assert!(matches!(
            &operations[1],
            RenderDiff::SetSkyBackground {
                background: Some(background)
            } if background.texture == texture.id
        ));
        for operation in operations {
            operation.validate().unwrap();
        }
    }
}
