// Surface moss (#10044): the damp under the moss. One shader marks where moss grows on every stone place and
// field stone, from where it stands, as a soft damp stain: the stone's own painted texture darkened and greened,
// never painted over, so its texels and cracks show through. The moss itself is painted cards
// (Modules/Places/PlaceMossCards: ivy curtains, flat mats, cushions) grown from the same field, where it is
// strongest; the stain reaches a little wider, so every card sits on a damp patch that fades out around it.
//
// Water and light sit most on up-facing surfaces, some on sides in patches stretched downward as water runs,
// none underneath, in clumps from world-space noise read once per texel on the surface's dominant plane (the
// crunch rule's 1/32 m), so the stain's soft edge steps like the painted textures beside it.
//
// material.parameters:
//   [0] xyz: the frame origin (metres), wrapped to WRAP_METRES, so world_position + origin is a world point
//       (wrapped, so a far-flung world keeps texel precision); w: strength (0 none, 1 accents, 2 moss, 3 overgrown).
//   [1] rgb: the stain's tint, multiplying the stone where it is fullest; w: clump scale (metres per clump).
//   [2] x: how much of the tint the fullest stain takes (0 to 1); w: how far down a surface may face and still hold moss.
//   [3] w: how much a vertical face holds, as a share of a top's, plus 1000 times the texels per metre.

#import rusty::types::Surface
#import rusty::material::material
#import rusty::shade::standard_shade

const DEFAULT_TEXELS_PER_METRE: f32 = 32.0;
// The stain begins this far below the cards' threshold, and is full this far above it.
const STAIN_REACH: f32 = 0.08;
const STAIN_FULL: f32 = 0.06;
const PACK: f32 = 1000.0;

// Texels per metre, packed into parameters[3].w above the side share.
fn texels_per_metre() -> f32 {
    let packed = floor(material.parameters[3].w / PACK);
    return select(DEFAULT_TEXELS_PER_METRE, packed, packed > 0.0);
}
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
    let tpm = texels_per_metre();
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

fn shade(surface: Surface) -> vec4<f32> {
    var s = surface;
    let strength = material.parameters[0].w;
    if strength <= 0.0 {
        return standard_shade(s);
    }

    let p = surface.world_position + material.parameters[0].xyz;
    let n = normalize(surface.normal);
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

    // Tops hold the most; sides their share; undersides none.
    let lowest = material.parameters[2].w;
    let side = material.parameters[3].w - floor(material.parameters[3].w / PACK) * PACK;
    let facing = select(0.0, mix(side, 1.0, smoothstep(0.0, 0.8, n.y)), n.y >= lowest);
    let scale = max(material.parameters[1].w, 0.05);
    let m = moss_field(q, plane, facing, scale);
    // Strength lowers the threshold: accents at 1, moss at 2, overgrown at 3. The cards grow above the threshold;
    // the stain starts STAIN_REACH below it and is full STAIN_FULL above it.
    let threshold = 0.74 - strength * 0.09;
    let stain = smoothstep(threshold - STAIN_REACH, threshold + STAIN_FULL, m);
    if stain <= 0.0 {
        return standard_shade(s);
    }

    let tint = mix(vec3<f32>(1.0), material.parameters[1].rgb, stain * material.parameters[2].x);
    s.base = vec4<f32>(s.base.rgb * tint, s.base.a);
    // Damp stone is a little less rough.
    s.roughness = mix(s.roughness, s.roughness * 0.85, stain);
    return standard_shade(s);
}
