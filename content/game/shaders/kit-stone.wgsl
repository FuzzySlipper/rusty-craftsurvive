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
// material.parameters[2]: w the moss strength (#10092; 0 none, 2 moss, as SurfaceMoss's); xyz unused (the field is read in
//   the dungeon's own frame, which does not move while it is loaded).
// material.parameters[3]: x how much of the stain's tint the fullest stain takes, y how much a vertical face holds, z how
//   far down a surface may face and still hold moss (its normal's y).
// product_map_a: the same texture as the material's base colour.
//
// Moss (#10092): the places' damp stain (surface-moss.wgsl) on the kit, from the same moss field, so a ruin underground
// and one on the moors grow one pattern. The kit's grain is fixed below (its clump size and texels per metre: SurfaceMoss
// .PlaceGrain, kept equal). The moisture is each placement's dampness from the dungeon's moisture volume, carried in its
// appearance's texture tint as blue = 1 - dampness * WET_IN_BLUE (alpha must stay 1: below it the Engine draws the
// appearance as transparent), times the piece's rising damp (its vertex colour's alpha, wet at a wall's foot). The tint
// is divided back out of the base colour, so the painted stone keeps its own colour.

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

// --- The moss field (kept equal in surface-moss.wgsl, ivy-blanket.wgsl and SurfaceMoss.Field; the kit's grain fixed) ---
// The kit's grain (SurfaceMoss.PlaceGrain): texels per metre, the lattice in texels (broad clumps across and down a side,
// fine across and down), and the wrap period in metres every lattice divides.
const MOSS_TEXELS: f32 = 32.0;
const MOSS_LATTICE: vec4<i32> = vec4<i32>(29, 64, 11, 24);
const MOSS_PERIOD: i32 = 1914;
const EDGE_FRAY: f32 = 0.04;
// How wet a surface is for its moisture (0..1): a dungeon's air is 0.3 at its driest and 1 by the caves, and moss
// should hold from about the middle of that (the places' MOISTURE_SCALE, 1.25, is for their vertex moisture, near 1).
const MOISTURE_SCALE: f32 = 1.7;
// A placement's dampness (0..2: KitMoss.Dampness, overgrown by the caves) is carried in its tint's blue as
// 1 - dampness * WET_IN_BLUE.
const WET_IN_BLUE: f32 = 0.45;
// The stain begins this far below the cards' threshold, and is full this far above it; its tint (linear).
const STAIN_REACH: f32 = 0.08;
const STAIN_FULL: f32 = 0.06;
const STAIN_TINT: vec3<f32> = vec3<f32>(0.5, 0.64, 0.4);

fn moss_hash(c: vec2<i32>) -> f32 {
    var h = bitcast<u32>(c.x) * 374761393u + bitcast<u32>(c.y) * 668265263u;
    h = (h ^ (h >> 13u)) * 1274126177u;
    return f32(h & 0x00ffffffu) / 16777215.0;
}

fn floor_div(a: vec2<i32>, b: vec2<i32>) -> vec2<i32> {
    let q = a / b;
    return q - vec2<i32>(select(vec2<i32>(0), vec2<i32>(1), (a % b != vec2<i32>(0)) & (a < vec2<i32>(0))));
}

fn period_texels() -> i32 {
    return MOSS_PERIOD * i32(MOSS_TEXELS);
}

fn wrap(a: vec2<i32>, n: i32) -> vec2<i32> {
    return ((a % vec2<i32>(n)) + vec2<i32>(n)) % vec2<i32>(n);
}

fn lattice_noise(t: vec2<i32>, cells: vec2<i32>) -> f32 {
    let i = floor_div(t, cells);
    let f = (vec2<f32>(t - i * cells) + 0.5) / vec2<f32>(cells);
    let u = f * f * (3.0 - 2.0 * f);
    let n = vec2<i32>(period_texels()) / cells;
    let a = moss_hash(wrap(i, n.x) * vec2<i32>(1, 0) + wrap(i, n.y) * vec2<i32>(0, 1));
    let i1 = i + vec2<i32>(1, 1);
    let b = moss_hash(vec2<i32>(wrap(i1, n.x).x, wrap(i, n.y).y));
    let c = moss_hash(vec2<i32>(wrap(i, n.x).x, wrap(i1, n.y).y));
    let d = moss_hash(vec2<i32>(wrap(i1, n.x).x, wrap(i1, n.y).y));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

fn moss_field(t: vec2<i32>, top: bool) -> f32 {
    let l = MOSS_LATTICE;
    let broad = select(vec2<i32>(l.x, l.y), vec2<i32>(l.x, l.x), top);
    let fine = select(vec2<i32>(l.z, l.w), vec2<i32>(l.z, l.z), top);
    let clump = lattice_noise(t, broad) * 0.7 + lattice_noise(t + fine * vec2<i32>(17, 5), fine) * 0.3;
    let fray = (moss_hash(wrap(t, period_texels()) + vec2<i32>(1013, 7919)) - 0.5) * EDGE_FRAY;
    return clump + fray;
}

// Where water sits: tops hold the most, sides their share, undersides none; times the moisture (no sun underground).
fn wetness(n: vec3<f32>, moisture: f32) -> f32 {
    let side = material.parameters[3].y;
    let lowest = material.parameters[3].z;
    let facing = select(0.0, mix(side, 1.0, smoothstep(0.0, 0.8, n.y)), n.y >= lowest);
    return facing * moisture * MOISTURE_SCALE;
}

fn texel_of(world: vec3<f32>, n: vec3<f32>) -> vec3<i32> {
    let a = abs(n);
    var q: vec2<f32>;
    var top = 0;
    if a.y >= a.x && a.y >= a.z {
        q = world.xz;
        top = 1;
    } else if a.x >= a.z {
        q = world.zy;
    } else {
        q = world.xy;
    }
    return vec3<i32>(vec2<i32>(floor(q * MOSS_TEXELS)), top);
}
// --- end of the moss field ---

fn shade(surface: Surface) -> vec4<f32> {
    let facet = material.parameters[0];
    let calm = material.parameters[1];
    var s = surface;
    // The texture's own colour: the tint carries the placement's dampness, not a colour.
    let tint = max(surface.tint.rgb, vec3<f32>(0.001));
    s.base = vec4<f32>(surface.base.rgb / tint, surface.base.a);
    let moisture = clamp((1.0 - surface.tint.b) / WET_IN_BLUE, 0.0, 2.0) * surface.tint.a;
    let texels = surface.uv * facet.x;
    // How many texels one screen pixel spans here.
    let per_pixel = max(length(dpdx(texels)), length(dpdy(texels)));

    if (calm.w > 0.0) {
        let mean = textureSampleLevel(product_map_a, product_sampler_a, surface.uv, calm.z).rgb;
        let quiet = smoothstep(calm.x, calm.y, per_pixel) * calm.w;
        // Line work holds: a texel much darker than its neighbourhood (a joint, a crack, an outline) keeps its contrast,
        // while the small ups and downs around the mean (the speckle) are turned down.
        let shade_here = dot(s.base.rgb, LUMA);
        let shade_mean = max(dot(mean, LUMA), 0.0001);
        let line = smoothstep(LINE_FROM, LINE_FULL, 1.0 - shade_here / shade_mean);
        let turned = quiet * (1.0 - line);
        s.base = vec4<f32>(mean + (s.base.rgb - mean) * (1.0 - turned), s.base.a);
    }

    // The damp stain under the moss: the painted stone darkened and greened where the field is strong, its marks kept.
    let strength = material.parameters[2].w;
    if (strength > 0.0 && moisture > 0.0) {
        let n = normalize(surface.normal);
        let mt = texel_of(surface.world_position, n);
        let m = moss_field(mt.xy, mt.z == 1) * wetness(n, moisture);
        let threshold = 0.74 - strength * 0.09;
        let stain = smoothstep(threshold - STAIN_REACH, threshold + STAIN_FULL, m);
        s.base = vec4<f32>(s.base.rgb * mix(vec3<f32>(1.0), STAIN_TINT, stain * material.parameters[3].x), s.base.a);
        s.roughness = mix(s.roughness, s.roughness * 0.85, stain);
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
