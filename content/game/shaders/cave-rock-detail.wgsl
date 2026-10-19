// Cave rock detail (#10094): detail below the voxels' size, drawn on the smooth rock as light rather than geometry.
// Two layers bend the surface's normal before the standard shade lights it:
// - grain: the slope of a 3D value noise in world space, so the rock reads pitted and worn without a texture's repeat;
// - facets: the rock cut into small cells, each with one flat, tilted face, so light picks out stepped micro-facets
//   (the micro-voxel read) while the silhouette stays the smooth voxel surface.
// Only the normal changes: collision, shadows and silhouettes stay the voxels'.
//
// material.parameters[0]: x grain strength (0 none), y grain frequency (cycles per metre), z facet size (metres,
// 0 none), w facet strength (how far each facet tilts from the surface, 0 to 1).

#import rusty::types::Surface
#import rusty::material::material
#import rusty::shade::standard_shade

// How far apart the grain's slope is measured, as a share of its wavelength.
const GRAIN_STEP: f32 = 0.15;
// The facets' normals snap to this many steps per unit before tilting, so neighbouring facets on a curve step.
const FACET_STEPS: f32 = 3.0;

fn hash3(c: vec3<i32>) -> f32 {
    var h = bitcast<u32>(c.x) * 374761393u + bitcast<u32>(c.y) * 668265263u + bitcast<u32>(c.z) * 2246822519u;
    h = (h ^ (h >> 13u)) * 1274126177u;
    h = h ^ (h >> 16u);
    return f32(h & 0xffffu) / 65535.0;
}

fn value_noise(p: vec3<f32>) -> f32 {
    let i = vec3<i32>(floor(p));
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    let a = mix(hash3(i), hash3(i + vec3<i32>(1, 0, 0)), u.x);
    let b = mix(hash3(i + vec3<i32>(0, 1, 0)), hash3(i + vec3<i32>(1, 1, 0)), u.x);
    let c = mix(hash3(i + vec3<i32>(0, 0, 1)), hash3(i + vec3<i32>(1, 0, 1)), u.x);
    let d = mix(hash3(i + vec3<i32>(0, 1, 1)), hash3(i + vec3<i32>(1, 1, 1)), u.x);
    return mix(mix(a, b, u.y), mix(c, d, u.y), u.z);
}

// Two octaves, the second at a little over twice the frequency.
fn grain(p: vec3<f32>) -> f32 {
    return value_noise(p) + 0.45 * value_noise(p * 2.3 + vec3<f32>(17.0, 3.0, 41.0));
}

// The surface bent along the grain's slope (its gradient, less the part along the normal).
fn grained(n: vec3<f32>, p: vec3<f32>, strength: f32, frequency: f32) -> vec3<f32> {
    let q = p * frequency;
    let e = GRAIN_STEP;
    let g = vec3<f32>(
        grain(q + vec3<f32>(e, 0.0, 0.0)) - grain(q - vec3<f32>(e, 0.0, 0.0)),
        grain(q + vec3<f32>(0.0, e, 0.0)) - grain(q - vec3<f32>(0.0, e, 0.0)),
        grain(q + vec3<f32>(0.0, 0.0, e)) - grain(q - vec3<f32>(0.0, 0.0, e))) / (2.0 * e);
    let along = g - dot(g, n) * n;
    return normalize(n - strength * along);
}

// The surface as facets: the normal snapped to coarse steps, then tilted by a direction fixed per cell.
fn faceted(n: vec3<f32>, p: vec3<f32>, size: f32, strength: f32) -> vec3<f32> {
    let cell = vec3<i32>(floor(p / size));
    let snapped = normalize(round(n * FACET_STEPS) / FACET_STEPS + n * 0.001);
    let tilt = vec3<f32>(hash3(cell), hash3(cell + vec3<i32>(71, 13, 5)), hash3(cell + vec3<i32>(9, 57, 113))) * 2.0 - 1.0;
    let sideways = tilt - dot(tilt, snapped) * snapped;
    return normalize(snapped + strength * sideways);
}

fn shade(surface: Surface) -> vec4<f32> {
    let detail = material.parameters[0];
    var s = surface;
    if (detail.z > 0.0) {
        s.normal = faceted(s.normal, s.world_position, detail.z, detail.w);
    }
    if (detail.x > 0.0) {
        s.normal = grained(s.normal, s.world_position, detail.x, detail.y);
    }
    return standard_shade(s);
}
