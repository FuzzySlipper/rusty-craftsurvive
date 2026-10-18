// Surface moss (#10044): the damp under the moss. One shader marks where moss grows on every stone place and
// field stone, from where it stands, as a soft damp stain: the stone's own painted texture darkened and greened,
// never painted over, so its texels and cracks show through. The moss itself is painted cards
// (Modules/Places/PlaceMossCards: ivy curtains, flat mats, cushions, leaf clusters) and, on an overgrown place, the
// ivy blanket (ivy-blanket.wgsl), all grown from this same field where it is strongest; the stain reaches a little
// wider, so every card sits on a damp patch that fades out around it.
//
// material.parameters (SurfaceMoss):
//   [0] xyz: the frame origin (metres), wrapped by the material's period; w: strength (0 none, 2 moss; an
//       overgrown place adds its overgrowth).
//   [1] the lattice in texels per cell: broad clumps across, broad clumps down a side, fine across, fine down.
//   [2] x: texels per metre; y: how much a vertical face holds; z: how far down a surface may face and hold
//       moss; w: 1 when the vertex colour's alpha is the vertex's moisture.
//   [3] x: the wrap period (whole metres; every lattice divides it, in texels); w: how much of the tint the
//       fullest stain takes.

#import rusty::types::Surface
#import rusty::material::material
#import rusty::shade::standard_shade

// The stain begins this far below the cards' threshold, and is full this far above it.
const STAIN_REACH: f32 = 0.08;
const STAIN_FULL: f32 = 0.06;
// The stain's tint (linear), multiplying the stone where it is fullest: darker and a little green, so warm stone
// goes olive-grey under the moss without losing its painted marks.
const STAIN_TINT: vec3<f32> = vec3<f32>(0.5, 0.64, 0.4);

// --- The moss field (kept equal in surface-moss.wgsl, ivy-blanket.wgsl and SurfaceMoss.Field) ---
// The field is read once per texel of the surface's dominant plane. Its noise lattice counts whole texels
// (parameters[1]: texels per lattice cell for the broad clumps across and down, and for the second octave),
// and the material's origin is wrapped by a period every lattice divides, so the pattern never jumps when the
// frame origin moves.

// How much one texel's nudge can move a patch's edge, in the clump field's units.
const EDGE_FRAY: f32 = 0.04;
// A north face is this much wetter than an east or west one, a south face this much drier.
const SUN_DRYING: f32 = 0.15;
// The vertex moisture's alpha is moisture divided by this.
const MOISTURE_SCALE: f32 = 1.25;

fn hash(c: vec2<i32>) -> f32 {
    var h = bitcast<u32>(c.x) * 374761393u + bitcast<u32>(c.y) * 668265263u;
    h = (h ^ (h >> 13u)) * 1274126177u;
    return f32(h & 0x00ffffffu) / 16777215.0;
}

fn floor_div(a: vec2<i32>, b: vec2<i32>) -> vec2<i32> {
    let q = a / b;
    return q - vec2<i32>(select(vec2<i32>(0), vec2<i32>(1), (a % b != vec2<i32>(0)) & (a < vec2<i32>(0))));
}

// The wrap period in texels (parameters[3].x metres times texels per metre): the field repeats exactly over it.
fn period_texels() -> i32 {
    return i32(material.parameters[3].x) * i32(material.parameters[2].x);
}

fn wrap(a: vec2<i32>, n: i32) -> vec2<i32> {
    return ((a % vec2<i32>(n)) + vec2<i32>(n)) % vec2<i32>(n);
}

// Value noise on a lattice of `cells` texels per cell, sampled at a texel's centre, repeating over the period.
fn lattice_noise(t: vec2<i32>, cells: vec2<i32>) -> f32 {
    let i = floor_div(t, cells);
    let f = (vec2<f32>(t - i * cells) + 0.5) / vec2<f32>(cells);
    let u = f * f * (3.0 - 2.0 * f);
    let n = vec2<i32>(period_texels()) / cells;
    let a = hash(wrap(i, n.x) * vec2<i32>(1, 0) + wrap(i, n.y) * vec2<i32>(0, 1));
    let i1 = i + vec2<i32>(1, 1);
    let b = hash(vec2<i32>(wrap(i1, n.x).x, wrap(i, n.y).y));
    let c = hash(vec2<i32>(wrap(i, n.x).x, wrap(i1, n.y).y));
    let d = hash(vec2<i32>(wrap(i1, n.x).x, wrap(i1, n.y).y));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// The moss amount at texel t of a plane (0 top, else a side, whose clumps run downward), before facing.
fn moss_field(t: vec2<i32>, top: bool) -> f32 {
    let l = vec4<i32>(material.parameters[1]);
    let broad = select(vec2<i32>(l.x, l.y), vec2<i32>(l.x, l.x), top);
    let fine = select(vec2<i32>(l.z, l.w), vec2<i32>(l.z, l.z), top);
    let clump = lattice_noise(t, broad) * 0.7 + lattice_noise(t + fine * vec2<i32>(17, 5), fine) * 0.3;
    let fray = (hash(wrap(t, period_texels()) + vec2<i32>(1013, 7919)) - 0.5) * EDGE_FRAY;
    return clump + fray;
}

// Where water sits: tops hold the most, sides their share, undersides none; shaded north (-Z) faces are wetter and
// sunward south faces drier, around 1; and the vertex's own moisture when parameters[2].w is set (its colour alpha,
// baked from its height above ground as moisture / MOISTURE_SCALE, so the stored alpha stays within 0..1).
fn wetness(n: vec3<f32>, vertex_moisture: f32) -> f32 {
    let side = material.parameters[2].y;
    let lowest = material.parameters[2].z;
    let facing = select(0.0, mix(side, 1.0, smoothstep(0.0, 0.8, n.y)), n.y >= lowest);
    let sun = 1.0 - SUN_DRYING * n.z;
    let own = select(1.0, vertex_moisture * MOISTURE_SCALE, material.parameters[2].w > 0.5);
    return facing * sun * own;
}

fn texel_of(world: vec3<f32>, n: vec3<f32>) -> vec3<i32> {
    let p = world + material.parameters[0].xyz;
    let a = abs(n);
    var q: vec2<f32>;
    var top = 0;
    if a.y >= a.x && a.y >= a.z {
        q = p.xz;
        top = 1;
    } else if a.x >= a.z {
        q = p.zy;
    } else {
        q = p.xy;
    }
    let t = vec2<i32>(floor(q * material.parameters[2].x));
    return vec3<i32>(t, top);
}

fn field_at(world: vec3<f32>, n: vec3<f32>, vertex_moisture: f32) -> f32 {
    let t = texel_of(world, n);
    return moss_field(t.xy, t.z == 1) * wetness(n, vertex_moisture);
}
// --- end of the moss field ---

fn shade(surface: Surface) -> vec4<f32> {
    var s = surface;
    let strength = material.parameters[0].w;
    if strength <= 0.0 {
        return standard_shade(s);
    }

    let m = field_at(surface.world_position, normalize(surface.normal), surface.tint.a);
    // Strength lowers the threshold. The cards grow above it; the stain starts STAIN_REACH below it.
    let threshold = 0.74 - strength * 0.09;
    let stain = smoothstep(threshold - STAIN_REACH, threshold + STAIN_FULL, m);
    if stain <= 0.0 {
        return standard_shade(s);
    }

    let tint = mix(vec3<f32>(1.0), STAIN_TINT, stain * material.parameters[3].w);
    s.base = vec4<f32>(s.base.rgb * tint, s.base.a);
    // Damp stone is a little less rough.
    s.roughness = mix(s.roughness, s.roughness * 0.85, stain);
    return standard_shade(s);
}
