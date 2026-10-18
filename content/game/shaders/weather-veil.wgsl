// The weather's veil over the view (CameraView.SetImageEffect, #9740): the glass storm's air ripples slowly and
// splits light into faint prismatic fringes, as much as effect_parameter(0).x (the weather's arcane channel, 0 to
// 1); and in bitter cold (#10068) frost creeps in from the picture's edges, as much as effect_parameter(0).y.
#import rusty::image::{ImagePixel, effect_parameter, picture_at}

const RIPPLE_UV: f32 = 0.0035;
const FRINGE_UV: f32 = 0.0025;

// Frost is drawn in blocks of this grid over the picture (about square on a wide screen), so it reads as painted
// crystals, not a blur. It reaches at most FROST_REACH of the picture in from each edge; a block is frosted where its
// nearness to the edge beats its own noise, so the frost's inner edge is ragged. FROST_WHITE is display colour.
const FROST_GRID: vec2<f32> = vec2<f32>(192.0, 108.0);
const FROST_REACH: f32 = 0.5;
const FROST_WHITE: vec3<f32> = vec3<f32>(0.86, 0.91, 0.97);
const FROST_COVER: f32 = 0.72;
const FROST_SPARKLE: f32 = 0.12;
// The edges' middles take this share of the corners' reach; and the noise breaks the inner edge up by this much.
const FROST_EDGE_SHARE: f32 = 0.15;
const FROST_RAGGED: f32 = 1.0;

fn frost_hash(c: vec2<i32>) -> f32 {
    var h = bitcast<u32>(c.x) * 374761393u + bitcast<u32>(c.y) * 668265263u;
    h = (h ^ (h >> 13u)) * 1274126177u;
    return f32(h & 0x00ffffffu) / 16777215.0;
}

// Smooth noise over blocks of `cells` grid blocks, so the frost grows in fronds rather than single specks.
fn frost_noise(b: vec2<i32>, cells: i32) -> f32 {
    let i = vec2<i32>(floor(vec2<f32>(b) / f32(cells)));
    let f = (vec2<f32>(b - i * cells) + 0.5) / f32(cells);
    let u = f * f * (3.0 - 2.0 * f);
    let a = frost_hash(i);
    let c = frost_hash(i + vec2<i32>(1, 0));
    let d = frost_hash(i + vec2<i32>(0, 1));
    let e = frost_hash(i + vec2<i32>(1, 1));
    return mix(mix(a, c, u.x), mix(d, e, u.x), u.y);
}

fn image_effect(pixel: ImagePixel) -> vec4<f32> {
    let arcane = effect_parameter(0u).x;
    let frost = effect_parameter(0u).y;
    let t = pixel.time;
    let ripple = vec2<f32>(sin(pixel.uv.y * 38.0 + t * 1.7), cos(pixel.uv.x * 31.0 - t * 1.3)) * RIPPLE_UV * arcane;
    let fringe = vec2<f32>(FRINGE_UV * arcane, 0.0);
    let at = pixel.uv + ripple;
    var colour = vec3<f32>(picture_at(at + fringe).r, picture_at(at).g, picture_at(at - fringe).b);

    if frost > 0.0 {
        let b = vec2<i32>(floor(pixel.uv * FROST_GRID));
        let centre = (vec2<f32>(b) + 0.5) / FROST_GRID;
        let ex = min(centre.x, 1.0 - centre.x);
        let ey = min(centre.y, 1.0 - centre.y);
        // Frost gathers in the corners: it reaches FROST_EDGE_SHARE as far in at an edge's middle as in a corner.
        let corner = 1.0 - 2.0 * max(ex, ey);
        let reach = FROST_REACH * frost * mix(FROST_EDGE_SHARE, 1.0, corner * corner);
        let n = frost_noise(b, 6) * 0.65 + frost_noise(b + vec2<i32>(101, 37), 2) * 0.35;
        let near = 1.0 - min(ex, ey) / max(reach, 0.0001);
        if near > n * FROST_RAGGED {
            let sparkle = (frost_hash(b + vec2<i32>(7, 911)) - 0.5) * FROST_SPARKLE;
            let deep = clamp((near - n * FROST_RAGGED) * 3.0, 0.35, 1.0);
            colour = mix(colour, FROST_WHITE + vec3<f32>(sparkle), FROST_COVER * deep);
        }
    }

    return vec4<f32>(colour, pixel.color.a);
}
