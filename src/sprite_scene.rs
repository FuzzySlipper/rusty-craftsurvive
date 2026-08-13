use rusty_engine::render_model::{
    BillboardMode, LightDescriptor, LightShadowIntent, RenderDiff, RenderHandle, RenderMetadata,
    SpriteAlphaMode, SpriteAtlasDescriptor, SpriteAttachment, SpriteDepthPolicy, SpriteFrameRect,
    SpriteInstanceDescriptor, SpriteLightingMode, SpriteMaterialDescriptor, SpriteShading,
    SpriteShadowPolicy, SpriteSizeMode, TextureDescriptor, TextureFilter, TexturePayloadSource,
    TextureWrap, Transform,
};

const WISP_BYTES: &[u8] = include_bytes!("../content/textures/source/forest-wisp.png");
pub const WISP_URL: &str = "/assets/forest-wisp.png";

const WISP_TEXTURE_ID: &str = "texture/craftsurvive-forest-wisp";
const WISP_ATLAS_ID: &str = "sprite-sheet/craftsurvive-forest-wisp";
const UNLIT_WISP_HANDLE: RenderHandle = RenderHandle::new(8_000_001);
const LIT_WISP_HANDLE: RenderHandle = RenderHandle::new(8_000_002);
const WISP_AMBIENT_HANDLE: RenderHandle = RenderHandle::new(8_000_003);
const WISP_POINT_HANDLE: RenderHandle = RenderHandle::new(8_000_004);

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WispTextureResource {
    pub identity: String,
    pub content_hash: String,
    pub media_type: &'static str,
    pub url: &'static str,
    pub bytes: &'static [u8],
}

pub fn wisp_texture_resource() -> Result<WispTextureResource, String> {
    let texture = wisp_texture_descriptor()?;
    let payload = texture
        .payload
        .as_ref()
        .ok_or_else(|| "wisp texture descriptor has no retained payload".to_owned())?;
    let TexturePayloadSource::Resource { resource } = &payload.source else {
        return Err("wisp texture must use a retained resource".to_owned());
    };
    Ok(WispTextureResource {
        identity: resource.clone(),
        content_hash: payload.content_hash.clone(),
        media_type: "image/png",
        url: WISP_URL,
        bytes: WISP_BYTES,
    })
}

pub fn wisp_scene_ops() -> Result<Vec<RenderDiff>, String> {
    let texture = wisp_texture_descriptor()?;
    let atlas = SpriteAtlasDescriptor {
        id: WISP_ATLAS_ID.to_owned(),
        texture: texture.id.clone(),
        frames: vec![SpriteFrameRect {
            frame: 0,
            uv_min: [0.0, 0.0],
            uv_max: [1.0, 1.0],
            size: None,
        }],
    };
    Ok(vec![
        RenderDiff::DefineTexture { texture },
        RenderDiff::DefineSpriteAtlas {
            atlas: atlas.clone(),
        },
        RenderDiff::CreateLight {
            handle: WISP_AMBIENT_HANDLE,
            parent: None,
            light: LightDescriptor::Ambient {
                color: [0.34, 0.48, 0.62],
                intensity: 0.45,
                enabled: true,
                shadow_intent: LightShadowIntent::Disabled,
            },
        },
        RenderDiff::CreateLight {
            handle: WISP_POINT_HANDLE,
            parent: None,
            light: LightDescriptor::Point {
                color: [0.35, 0.82, 1.0],
                intensity: 22.0,
                enabled: true,
                position: [2.2, 5.3, 4.0],
                range: Some(7.5),
                decay: 2.0,
                shadow_intent: LightShadowIntent::Disabled,
            },
        },
        RenderDiff::CreateSprite {
            handle: UNLIT_WISP_HANDLE,
            parent: None,
            sprite: wisp_sprite(&atlas, [1.1, 4.5, 4.0], false),
        },
        RenderDiff::CreateSprite {
            handle: LIT_WISP_HANDLE,
            parent: None,
            sprite: wisp_sprite(&atlas, [2.2, 4.5, 4.0], true),
        },
    ])
}

fn wisp_texture_descriptor() -> Result<TextureDescriptor, String> {
    TextureDescriptor::admit_png_rgba8_resource(
        WISP_TEXTURE_ID.to_owned(),
        WISP_BYTES,
        TextureFilter::Nearest,
        TextureWrap::Clamp,
        1,
    )
    .map_err(|error| format!("admit forest wisp texture: {error:?}"))
}

fn wisp_sprite(
    atlas: &SpriteAtlasDescriptor,
    translation: [f32; 3],
    lit: bool,
) -> SpriteInstanceDescriptor {
    SpriteInstanceDescriptor {
        asset: atlas.id.clone(),
        frame: 0,
        pivot: [0.5, 0.5],
        size: [0.65, 0.65],
        size_mode: SpriteSizeMode::World,
        billboard: BillboardMode::Cylindrical,
        tint: [1.0; 4],
        render_order: 3,
        depth: SpriteDepthPolicy::DepthWriteOff,
        shading: if lit {
            SpriteShading::Lit
        } else {
            SpriteShading::Unlit
        },
        material: SpriteMaterialDescriptor {
            lighting: if lit {
                SpriteLightingMode::DerivedGradient
            } else {
                SpriteLightingMode::Unlit
            },
            normal_strength: if lit { 1.3 } else { 1.0 },
            normal_bias: if lit { 0.12 } else { 0.0 },
            alpha: SpriteAlphaMode::Mask { cutoff: 0.28 },
            shadow: SpriteShadowPolicy::None,
            ..SpriteMaterialDescriptor::default()
        },
        visible: true,
        transform: Transform {
            translation,
            ..Transform::IDENTITY
        },
        attachment: SpriteAttachment::default(),
        metadata: RenderMetadata {
            source_entity: None,
            source_scene_node: None,
            tags: vec![
                "craftsurvive-wisp-comparison".to_owned(),
                if lit { "lit" } else { "unlit" }.to_owned(),
            ],
            label: Some(if lit {
                "Lit forest wisp (derived gradient)".to_owned()
            } else {
                "Unlit forest wisp reference".to_owned()
            }),
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fixture_is_rgba_and_scene_uses_public_lit_sprite_contract() {
        let resource = wisp_texture_resource().unwrap();
        assert_eq!(resource.bytes, WISP_BYTES);
        assert_eq!(
            &resource.bytes[24..26],
            &[8, 6],
            "fixture must be PNG RGBA8"
        );

        let operations = wisp_scene_ops().unwrap();
        assert_eq!(operations.len(), 6);
        for operation in &operations {
            operation.validate().unwrap();
        }
        let sprites = operations
            .iter()
            .filter_map(|operation| match operation {
                RenderDiff::CreateSprite { sprite, .. } => Some(sprite),
                _ => None,
            })
            .collect::<Vec<_>>();
        assert_eq!(sprites.len(), 2);
        assert_eq!(sprites[0].material.lighting, SpriteLightingMode::Unlit);
        assert_eq!(
            sprites[1].material.lighting,
            SpriteLightingMode::DerivedGradient
        );
        assert!(sprites.iter().all(|sprite| {
            sprite.billboard == BillboardMode::Cylindrical
                && sprite.material.alpha == (SpriteAlphaMode::Mask { cutoff: 0.28 })
        }));
    }
}
