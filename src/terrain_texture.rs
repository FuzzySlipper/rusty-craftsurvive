use rusty_engine::render_model::{
    MaterialUvStrategy, RenderDiff, RenderMaterialDescriptor, TextureDescriptor, TextureFilter,
    TexturePayloadSource, TextureWrap, VoxelAtlasPaddingDescriptor, VoxelAtlasRegionDescriptor,
    VoxelSurfaceAlphaModeDescriptor, VoxelSurfaceDescriptor, VoxelSurfaceMappingDescriptor,
};

const TERRAIN_ATLAS_BYTES: &[u8] = include_bytes!("../content/textures/terrain-atlas.png");
pub const TERRAIN_ATLAS_URL: &str = "/assets/terrain-atlas.png";

const TEXTURE_ID: &str = "texture/craftsurvive-terrain-atlas";
const ATLAS_ID: &str = "sprite-sheet/craftsurvive-terrain-atlas";
const TILE_EXTENT: [u32; 2] = [64, 64];

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TerrainTextureResource {
    pub identity: String,
    pub content_hash: String,
    pub media_type: &'static str,
    pub url: &'static str,
    pub bytes: &'static [u8],
}

pub fn terrain_texture_resource() -> Result<TerrainTextureResource, String> {
    let texture = terrain_texture_descriptor()?;
    let payload = texture
        .payload
        .as_ref()
        .ok_or_else(|| "terrain texture descriptor has no retained payload".to_owned())?;
    let TexturePayloadSource::Resource { resource } = &payload.source else {
        return Err("terrain texture must use a retained resource".to_owned());
    };
    Ok(TerrainTextureResource {
        identity: resource.clone(),
        content_hash: payload.content_hash.clone(),
        media_type: "image/png",
        url: TERRAIN_ATLAS_URL,
        bytes: TERRAIN_ATLAS_BYTES,
    })
}

pub fn terrain_material_ops() -> Result<Vec<RenderDiff>, String> {
    let texture = terrain_texture_descriptor()?;
    let definitions = [
        (1, "grass-top", [0, 0]),
        (2, "dirt", [0, 64]),
        (3, "stone", [64, 64]),
        (4, "grass-side", [64, 0]),
    ];
    let mut operations = vec![RenderDiff::DefineTexture {
        texture: texture.clone(),
    }];
    operations.extend(
        definitions.map(|(slot, name, content_min)| RenderDiff::DefineMaterial {
            material: terrain_material(&texture, slot, name, content_min),
        }),
    );
    Ok(operations)
}

fn terrain_texture_descriptor() -> Result<TextureDescriptor, String> {
    TextureDescriptor::admit_png_rgba8_resource(
        TEXTURE_ID.to_owned(),
        TERRAIN_ATLAS_BYTES,
        TextureFilter::Nearest,
        TextureWrap::Clamp,
        1,
    )
    .map_err(|error| format!("admit terrain texture atlas: {error:?}"))
}

fn terrain_material(
    texture: &TextureDescriptor,
    slot: u16,
    name: &str,
    content_min: [u32; 2],
) -> RenderMaterialDescriptor {
    let content_hash = texture
        .content_hash
        .clone()
        .expect("admitted terrain texture has a content hash");
    RenderMaterialDescriptor {
        schema_version: 2,
        id: format!("voxel-material/{slot}"),
        color: [1.0; 4],
        texture: Some(texture.id.clone()),
        roughness: 0.92,
        texture_tint: [1.0; 4],
        emission_color: [0.0; 3],
        emission_intensity: 0.0,
        uv_strategy: MaterialUvStrategy::Atlas,
        voxel_surface: Some(VoxelSurfaceDescriptor {
            schema_version: 1,
            filter: TextureFilter::Nearest,
            wrap: TextureWrap::Clamp,
            alpha_mode: VoxelSurfaceAlphaModeDescriptor::Opaque,
            mapping: VoxelSurfaceMappingDescriptor::Atlas {
                atlas: ATLAS_ID.to_owned(),
                atlas_version: 1,
                atlas_content_hash: content_hash.clone(),
                texture: texture.id.clone(),
                texture_version: texture.version,
                texture_content_hash: content_hash,
                region: VoxelAtlasRegionDescriptor {
                    id: name.to_owned(),
                    content_min,
                    content_extent: TILE_EXTENT,
                    padding: VoxelAtlasPaddingDescriptor {
                        left: 0,
                        right: 0,
                        bottom: 0,
                        top: 0,
                    },
                    inset: "halfTexel".to_owned(),
                },
                tile_scale_cells: [1.0, 1.0],
                tile_origin_cells: [0.0, 0.0],
            },
        }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn atlas_resource_and_every_material_validate_with_bounded_regions() {
        let resource = terrain_texture_resource().unwrap();
        assert_eq!(resource.media_type, "image/png");
        assert!(resource.identity.starts_with("texture-resource/"));
        assert_eq!(resource.bytes, TERRAIN_ATLAS_BYTES);

        let operations = terrain_material_ops().unwrap();
        assert_eq!(operations.len(), 5);
        for operation in operations {
            match operation {
                RenderDiff::DefineTexture { texture } => texture.validate().unwrap(),
                RenderDiff::DefineMaterial { material } => material.validate().unwrap(),
                other => panic!("unexpected terrain operation: {other:?}"),
            }
        }
    }
}
