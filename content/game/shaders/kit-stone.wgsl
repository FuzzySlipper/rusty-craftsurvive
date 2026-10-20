// Kit stone (#10089): the dungeon kit's painted texels drawn as 3D pixel art, and calmed with distance.
//
// - Texel facets: every texel of the painted texture is a small flat facet, its normal tilted a little (part its own,
//   part a swell shared with its neighbours), so the light picks the painted texels out as lit 3D pixels, as the cave
//   rock's facets do (cave-rock-detail.wgsl). Only the normal changes. The facets fade as a texel shrinks toward a
//   screen pixel, so they never shimmer.
// - Calming: as texels shrink on screen, each texel's difference from its neighbourhood's mean (the texture's own
//   coarser mip, product_map_a) is turned down, not blurred, except where a texel is much darker than the mean: the
//   broad colour and the painted line work (joints, bold cracks) hold, while the fine speckle that would read as
//   noise at distance quietens.
//
// material.parameters[0]: x the texture's texels across (its width), y facet strength (0 none, 1 strong), z the steps a
//   facet's normal snaps to (0 none), w how much neighbouring facets lean together (0 each its own, 1 a smooth swell).
// material.parameters[1]: x the texels per screen pixel where calming begins, y where it is full, z the mip the
//   neighbourhood's mean is read from, w how far a texel's difference from the mean is turned down at full calm (0..1).
// product_map_a: the same texture as the material's base colour.

#import rusty::types::Surface
#import rusty::material::{material, product_map_a, product_sampler_a}
#import rusty::shade::standard_shade

// A facet is this many screen pixels across at least before it has faded away.
const PIXELS_PER_FACET: f32 = 2.5;
// Calming keeps a texel's contrast once it is this much darker than its neighbourhood (a share of the mean's
// brightness), fully by the second: the painted line work.
const LINE_FROM: f32 = 0.25;
const LINE_FULL: f32 = 0.45;
const LUMA: vec3<f32> = vec3<f32>(0.2126, 0.7152, 0.0722);
// The shared part of a facet's lean varies over this many texels.
const SWELL_TEXELS: f32 = 6.0;

fn hash2(c: vec2<i32>, salt: u32) -> f32 {
    var h = bitcast<u32>(c.x) * 374761393u + bitcast<u32>(c.y) * 668265263u + salt * 2246822519u;
    h = (h ^ (h >> 13u)) * 1274126177u;
    h = h ^ (h >> 16u);
    return f32(h & 0xffffu) / 65535.0;
}

fn smooth_noise(p: vec2<f32>, salt: u32) -> f32 {
    let i = vec2<i32>(floor(p));
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    let a = mix(hash2(i, salt), hash2(i + vec2<i32>(1, 0), salt), u.x);
    let b = mix(hash2(i + vec2<i32>(0, 1), salt), hash2(i + vec2<i32>(1, 1), salt), u.x);
    return mix(a, b, u.y);
}

fn shade(surface: Surface) -> vec4<f32> {
    let facet = material.parameters[0];
    let calm = material.parameters[1];
    var s = surface;
    let texels = surface.uv * facet.x;
    // How many texels one screen pixel spans here.
    let per_pixel = max(length(dpdx(texels)), length(dpdy(texels)));

    if (calm.w > 0.0) {
        let mean = textureSampleLevel(product_map_a, product_sampler_a, surface.uv, calm.z).rgb * surface.tint.rgb;
        let quiet = smoothstep(calm.x, calm.y, per_pixel) * calm.w;
        // Line work holds: a texel much darker than its neighbourhood (a joint, a crack, an outline) keeps its contrast,
        // while the small ups and downs around the mean (the speckle) are turned down.
        let shade_here = dot(s.base.rgb, LUMA);
        let shade_mean = max(dot(mean, LUMA), 0.0001);
        let line = smoothstep(LINE_FROM, LINE_FULL, 1.0 - shade_here / shade_mean);
        let turned = quiet * (1.0 - line);
        s.base = vec4<f32>(mean + (s.base.rgb - mean) * (1.0 - turned), s.base.a);
    }

    if (facet.y > 0.0) {
        let texel = vec2<i32>(floor(texels));
        // Full while a texel spans twice the least, gone once it spans only PIXELS_PER_FACET pixels.
        let keep = 1.0 - smoothstep(0.5 / PIXELS_PER_FACET, 1.0 / PIXELS_PER_FACET, per_pixel);
        let n = normalize(surface.normal);
        var base_n = n;
        if (facet.z > 0.0) {
            base_n = normalize(round(n * facet.z) / facet.z + n * 0.001);
        }
        let own = vec3<f32>(hash2(texel, 1u), hash2(texel, 2u), hash2(texel, 3u));
        let centre = (vec2<f32>(texel) + 0.5) / SWELL_TEXELS;
        let swell = vec3<f32>(smooth_noise(centre, 4u), smooth_noise(centre, 5u), smooth_noise(centre, 6u));
        let tilt = mix(own, swell, facet.w) * 2.0 - 1.0;
        let sideways = tilt - dot(tilt, base_n) * base_n;
        s.normal = normalize(mix(n, normalize(base_n + facet.y * sideways), keep));
    }

    return standard_shade(s);
}
