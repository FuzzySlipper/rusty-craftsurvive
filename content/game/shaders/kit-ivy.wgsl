// Kit ivy (#10092): the places' ivy blanket (ivy-blanket.wgsl) on the dungeon kit - a thin shell over a wet piece's walls
// carrying the seamless painted ivy mat (its base texture), shown only where the kit's moss field is very strong, so a wall
// by the caves is more ivy than stone. It reads the same field and moisture as kit-stone.wgsl (each placement's dampness in
// its tint's blue, times the piece's rising damp in the vertex alpha), so the blanket, the damp stain and the moss cards are
// one pattern. Near its edge the dark gaps between the painted leaves are cut too, so it breaks up into leaves.
//
// material.parameters[0]: x the field the blanket shows above. [3]: y how much a vertical face holds, z how far down a
// surface may face and still hold it (as kit-stone.wgsl's).
//
// The blanket casts no shadow: the shadow pass does not see a placement's tint, so it could not cut the shell the same way.

#import rusty::types::Surface
#import rusty::material::material
#import rusty::shade::standard_shade

// Within this much of the threshold, leaf gaps (dark texels of the ivy) are cut as well; GAP_LUMINANCE is linear.
const LEAFY_EDGE: f32 = 0.18;
const GAP_LUMINANCE: f32 = 0.02;

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
    let tint = max(surface.tint.rgb, vec3<f32>(0.001));
    let moisture = clamp((1.0 - surface.tint.b) / WET_IN_BLUE, 0.0, 2.0) * surface.tint.a;
    let n = normalize(surface.normal);
    let t = texel_of(surface.world_position, n);
    let m = moss_field(t.xy, t.z == 1) * wetness(n, moisture);
    let threshold = material.parameters[0].x;
    if m < threshold {
        discard;
    }

    var s = surface;
    s.base = vec4<f32>(surface.base.rgb / tint, 1.0);
    let luminance = dot(s.base.rgb, vec3<f32>(0.2126, 0.7152, 0.0722));
    if m < threshold + LEAFY_EDGE && luminance < GAP_LUMINANCE {
        discard;
    }

    s.metalness = 0.0;
    return standard_shade(s);
}
