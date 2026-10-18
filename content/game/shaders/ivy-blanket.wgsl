// Ivy blanket (#10044): on an overgrown place, a thin shell over its walls carries a seamless painted ivy mat
// (the base texture), shown only where the moss field is very strong, so the wall is more growth than stone.
// It reads the surface moss's own field (surface-moss.wgsl, the helpers below are kept equal to it), so the
// blanket, the damp stain and the moss cards are one pattern. Near the blanket's edge the dark gaps between
// painted leaves are cut away too, so the edge breaks up into leaves instead of ending on a line.
//
// material.parameters:
//   [0] xyz: the frame origin (metres), wrapped; w: the place's moss strength (an overgrown place is 5 to 8).
//   [1] x: clump scale (metres per clump); y: texels per metre; z: how much a vertical face holds; w: lowest facing.

#import rusty::types::{Surface, Caster}
#import rusty::material::material
#import rusty::shade::standard_shade

// The blanket shows where the field is above BLANKET_AT_NONE - strength * BLANKET_PER_STRENGTH: never at the
// ordinary strength (2), over most of a wall at 2 plus an overgrowth of 6. GAP_LUMINANCE is linear: the tile's darkest tenth.
const BLANKET_AT_NONE: f32 = 1.40;
const BLANKET_PER_STRENGTH: f32 = 0.12;
// Within this much of the threshold, leaf gaps (dark texels of the ivy) are cut as well.
const LEAFY_EDGE: f32 = 0.10;
const GAP_LUMINANCE: f32 = 0.02;

const WRAP_CELLS: i32 = 4096;

fn hash(c: vec2<i32>) -> f32 {
    // Periodic in WRAP_CELLS so the origin's wrap leaves no seam.
    let w = vec2<i32>(((c.x % WRAP_CELLS) + WRAP_CELLS) % WRAP_CELLS, ((c.y % WRAP_CELLS) + WRAP_CELLS) % WRAP_CELLS);
    var h = u32(w.x) * 374761393u + u32(w.y) * 668265263u;
    h = (h ^ (h >> 13u)) * 1274126177u;
    return f32(h & 0x00ffffffu) / 16777215.0;
}

fn noise(p: vec2<f32>) -> f32 {
    let i = vec2<i32>(floor(p));
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    let a = hash(i);
    let b = hash(i + vec2<i32>(1, 0));
    let c = hash(i + vec2<i32>(0, 1));
    let d = hash(i + vec2<i32>(1, 1));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// Clumps: two octaves, the second breaking up the first's round edges.
fn clumps(p: vec2<f32>) -> f32 {
    return noise(p) * 0.7 + noise(p * 2.7 + vec2<f32>(17.3, 5.1)) * 0.3;
}

// How far a side face's patches stretch downward, as water runs down a wall.
const STREAK: f32 = 2.2;
// How much one texel's nudge can move a patch's edge, in the clump field's units.
const EDGE_FRAY: f32 = 0.04;

// The moss amount at one texel of the surface's dominant plane, before the threshold.
fn moss_field(q: vec2<f32>, plane: f32, facing: f32, scale: f32) -> f32 {
    let tpm = material.parameters[1].y;
    let texel = (floor(q * tpm) + 0.5) / tpm;
    // A side plane's second axis is height: its clumps run downward.
    let stretch = select(vec2<f32>(1.0, STREAK), vec2<f32>(1.0, 1.0), plane == 0.0);
    // Every plane reads the same noise field, so a patch carries on across a facet edge instead of
    // stopping there (a low-poly stone's facets otherwise turn all moss or bare, a mosaic).
    let clump = clumps(texel / (scale * stretch));
    // A per-texel nudge frays a patch's edge by a texel or two, as moss grows, instead of a smooth blob.
    let cell = vec2<i32>(floor(q * tpm));
    let fray = (hash(cell) - 0.5) * EDGE_FRAY;
    return (clump + fray) * facing;
}


fn blanket_field(world: vec3<f32>, n: vec3<f32>) -> f32 {
    let p = world + material.parameters[0].xyz;
    let a = abs(n);
    var q: vec2<f32>;
    var plane: f32;
    if a.y >= a.x && a.y >= a.z {
        q = p.xz;
        plane = 0.0;
    } else if a.x >= a.z {
        q = p.zy;
        plane = 1.0;
    } else {
        q = p.xy;
        plane = 2.0;
    }

    let facing = select(0.0, mix(material.parameters[1].z, 1.0, smoothstep(0.0, 0.8, n.y)), n.y >= material.parameters[1].w);
    return moss_field(q, plane, facing, max(material.parameters[1].x, 0.05));
}

fn blanket_threshold() -> f32 {
    return BLANKET_AT_NONE - material.parameters[0].w * BLANKET_PER_STRENGTH;
}

fn shade(surface: Surface) -> vec4<f32> {
    let m = blanket_field(surface.world_position, normalize(surface.normal));
    let threshold = blanket_threshold();
    if m < threshold {
        discard;
    }

    let luminance = dot(surface.base.rgb, vec3<f32>(0.2126, 0.7152, 0.0722));
    if m < threshold + LEAFY_EDGE && luminance < GAP_LUMINANCE {
        discard;
    }

    var s = surface;
    s.metalness = 0.0;
    return standard_shade(s);
}

// The shadow keeps the same cut, so the stone showing through a gap is lit. The caster has no normal: the
// surface's own, from its position's screen derivatives, serves (the shell has no undersides).
fn cast_shadow(caster: Caster) {
    let geometric = normalize(cross(dpdx(caster.world_position), dpdy(caster.world_position)));
    let n = select(-geometric, geometric, geometric.y >= 0.0);
    if blanket_field(caster.world_position, n) < blanket_threshold() {
        discard;
    }
}
