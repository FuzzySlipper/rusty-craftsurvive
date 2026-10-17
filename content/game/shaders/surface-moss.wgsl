// Surface moss (#10044): one shader grows moss on every stone place and field stone, so its style lives
// here and every ruin, stele and boulder gets its own moss from where it stands.
//
// Moss collects where water and light sit: most on up-facing surfaces, some on sides in patches stretched
// downward as water runs, none underneath; in clumps from world-space noise. It is
// drawn as pixel art at the crunch rule's scale: the noise is read once per 1/32 m texel on the surface's
// dominant plane, so a patch has crisp stepped edges like the painted textures beside it, and a patch's
// edge texels are darker, as a painter would outline it.
//
// material.parameters:
//   [0] xyz: the frame origin (metres), wrapped to WRAP_METRES, so world_position + origin is a world point
//       (wrapped, so a far-flung world keeps texel precision); w: strength (0 none, 1 accents, 2 moss, 3 overgrown).
//   [1] rgb: the moss's dark colour (linear); w: clump scale (metres per clump).
//   [2] rgb: the moss's light colour (linear); w: how far down a surface may face and still hold moss (normal y).
//   [3] rgb: the patch rim's colour (linear); w: how much a vertical face holds, as a share of a top's.

#import rusty::types::Surface
#import rusty::material::material
#import rusty::shade::standard_shade

const TEXELS_PER_METRE: f32 = 32.0;
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
    let texel = (floor(q * TEXELS_PER_METRE) + 0.5) / TEXELS_PER_METRE;
    // A side plane's second axis is height: its clumps run downward.
    let stretch = select(vec2<f32>(1.0, STREAK), vec2<f32>(1.0, 1.0), plane == 0.0);
    // Each plane reads its own part of the noise, so a side face does not repeat the top's pattern.
    let clump = clumps(texel / (scale * stretch) + vec2<f32>(plane * 31.7, plane * 11.3));
    // A per-texel nudge frays a patch's edge by a texel or two, as moss grows, instead of a smooth blob.
    let cell = vec2<i32>(floor(q * TEXELS_PER_METRE)) + vec2<i32>(i32(plane) * 977, 0);
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
    let side = material.parameters[3].w;
    let facing = select(0.0, mix(side, 1.0, smoothstep(0.0, 0.8, n.y)), n.y >= lowest);
    let scale = max(material.parameters[1].w, 0.05);
    let m = moss_field(q, plane, facing, scale);
    // Strength lowers the threshold: accents at 1, moss at 2, overgrown at 3.
    let threshold = 0.74 - strength * 0.09;
    if m < threshold {
        return standard_shade(s);
    }

    // The rim: a texel next to one without moss (along the plane's axes) is the patch's outline.
    let step_m = 1.0 / TEXELS_PER_METRE;
    let rim = min(min(moss_field(q + vec2<f32>(step_m, 0.0), plane, facing, scale),
                      moss_field(q - vec2<f32>(step_m, 0.0), plane, facing, scale)),
                  min(moss_field(q + vec2<f32>(0.0, step_m), plane, facing, scale),
                      moss_field(q - vec2<f32>(0.0, step_m), plane, facing, scale))) < threshold;
    // Light and dark moss in broad areas, read from a coarser noise so they form a few patches, not speckle.
    let texel = (floor(q * TEXELS_PER_METRE) + 0.5) / TEXELS_PER_METRE;
    let tone = step(0.5, noise(texel / (scale * 0.45) + vec2<f32>(53.0, 7.0)));
    var colour = mix(material.parameters[1].rgb, material.parameters[2].rgb, tone);
    if rim {
        colour = material.parameters[3].rgb;
    }

    s.base = vec4<f32>(colour, s.base.a);
    s.roughness = 1.0;
    s.metalness = 0.0;
    return standard_shade(s);
}
