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

pub fn wisp_scene_ops(platform_position: [f64; 3]) -> Result<Vec<RenderDiff>, String> {
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
            handle: WISP_POINT_HANDLE,
            parent: None,
            light: wisp_point_light(platform_position),
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

pub fn wisp_light_update_op(platform_position: [f64; 3]) -> RenderDiff {
    RenderDiff::UpdateLight {
        handle: WISP_POINT_HANDLE,
        light: wisp_point_light(platform_position),
    }
}

fn wisp_point_light(platform_position: [f64; 3]) -> LightDescriptor {
    LightDescriptor::Point {
        color: [0.15, 0.72, 1.0],
        intensity: if platform_position[0] >= 0.0 {
            18.0
        } else {
            0.0
        },
        enabled: true,
        position: [2.2, 4.6, 4.35],
        range: Some(1.15),
        decay: 2.0,
        shadow_intent: LightShadowIntent::Disabled,
    }
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
        tint: if lit {
            [0.08, 0.1, 0.14, 1.0]
        } else {
            [1.0; 4]
        },
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
            normal_strength: if lit { 2.5 } else { 1.0 },
            normal_bias: if lit { -0.2 } else { 0.0 },
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

        let operations = wisp_scene_ops([0.0, 4.25, 9.0]).unwrap();
        assert_eq!(operations.len(), 5);
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

        let update = wisp_light_update_op([1.5, 4.25, 9.0]);
        update.validate().unwrap();
        let RenderDiff::UpdateLight {
            light:
                LightDescriptor::Point {
                    position,
                    intensity,
                    ..
                },
            ..
        } = update
        else {
            panic!("wisp light update must remain a retained point light");
        };
        assert_eq!(position, [2.2, 4.6, 4.35]);
        assert_eq!(intensity, 18.0);
        let RenderDiff::UpdateLight {
            light: LightDescriptor::Point { intensity, .. },
            ..
        } = wisp_light_update_op([-1.5, 4.25, 9.0])
        else {
            unreachable!();
        };
        assert_eq!(intensity, 0.0);
    }
}
