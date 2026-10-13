// The mesh UV carries horizontal current in metres/second. Surface texture coordinates are
// world anchored, as in Engine WATER. The Engine still supplies depth colour and shore foam.
#import rusty::types::Surface
#import rusty::view::frame
#import rusty::material::{material, product_map_b, product_sampler_b}
#import rusty::shade::standard_shade

const BROAD_METRES: f32 = 13.0;
const FINE_METRES: f32 = 3.7;
const PATCH_METRES: f32 = 23.0;
const WARP_METRES: f32 = 1.5;
const WIND_DRIFT: vec2<f32> = vec2<f32>(0.09, 0.035);
const FINE_DRIFT: vec2<f32> = vec2<f32>(-0.045, 0.07);
const FINE_WEIGHT: f32 = 0.32;
const CALM_STRENGTH: f32 = 0.45;
const FAST_CURRENT: f32 = 2.5;
const FOAM_SPEED_START: f32 = 0.65;
const FOAM_SPEED_FULL: f32 = 2.1;
const FOAM_PATCH_START: f32 = 0.64;
const FOAM_PATCH_FULL: f32 = 0.82;
const FOAM_ROUGHNESS: f32 = 0.85;
const FOAM_COLOR: vec3<f32> = vec3<f32>(0.73, 0.82, 0.76);
const DETAIL_FADE_START: f32 = 35.0;
const DETAIL_FADE_END: f32 = 150.0;
const HASH_DIRECTION: vec2<f32> = vec2<f32>(127.1, 311.7);
const HASH_MULTIPLIER: f32 = 43758.5453;
const WARP_OFFSET: vec2<f32> = vec2<f32>(7.0);

fn hash(p: vec2<f32>) -> f32 {
    return fract(sin(dot(p, HASH_DIRECTION)) * HASH_MULTIPLIER);
}

fn noise(p: vec2<f32>) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2<f32>(1.0, 0.0)), u.x),
        mix(hash(i + vec2<f32>(0.0, 1.0)), hash(i + vec2<f32>(1.0)), u.x), u.y);
}

fn ripple(uv: vec2<f32>) -> vec2<f32> {
    return textureSample(product_map_b, product_sampler_b, uv).xy * 2.0 - 1.0;
}

// Two short advections crossfade. Each becomes invisible before resetting, so neighbouring
// currents can bend the pattern without stretching it indefinitely or making it jump.
fn flowing_normal(p: vec2<f32>, current: vec2<f32>, time: f32, offset: f32) -> vec2<f32> {
    let cycle = material.parameters[0].w;
    let phase = fract(time / cycle + offset);
    let other = fract(phase + 0.5);
    let weight = 1.0 - abs(2.0 * phase - 1.0);
    return ripple((p - current * phase * cycle) / BROAD_METRES) * weight
        + ripple((p - current * other * cycle) / BROAD_METRES) * (1.0 - weight);
}

fn flowing_foam(p: vec2<f32>, current: vec2<f32>, time: f32, offset: f32) -> f32 {
    let cycle = material.parameters[0].w;
    let phase = fract(time / cycle + offset);
    let other = fract(phase + 0.5);
    let weight = 1.0 - abs(2.0 * phase - 1.0);
    return noise((p - current * phase * cycle) / FINE_METRES) * weight
        + noise((p - current * other * cycle) / FINE_METRES) * (1.0 - weight);
}

fn shade(surface: Surface) -> vec4<f32> {
    let mode = material.parameters[0].x;
    if mode < 0.5 { return standard_shade(surface); }
    let current = surface.uv;
    let speed = length(current);
    if mode > 1.5 {
        // Red = +X, green = +Z, blue = slow; brighter red/green means faster flow.
        return vec4<f32>(vec2<f32>(0.5) + clamp(current / FAST_CURRENT, vec2<f32>(-1.0), vec2<f32>(1.0)) * 0.5,
            1.0 - clamp(speed / FAST_CURRENT, 0.0, 1.0), 1.0);
    }
    let time = frame.time.x;
    let p = surface.world_position.xz + material.parameters[1].xy;
    let patches = noise(p / PATCH_METRES - WIND_DRIFT * time / PATCH_METRES);
    let warp = vec2<f32>(noise(p / PATCH_METRES), noise(p.yx / PATCH_METRES + WARP_OFFSET)) - 0.5;
    let broad = flowing_normal(p + warp * WARP_METRES, current + WIND_DRIFT, time, patches);
    // A rotated second scale; rotate its normal back into the water's world frame.
    let fine_p = vec2<f32>(p.y, -p.x) - FINE_DRIFT * time;
    let fine_sample = ripple(fine_p / FINE_METRES);
    let fine = vec2<f32>(-fine_sample.y, fine_sample.x);
    let distance_to_eye = distance(frame.camera.xyz, surface.world_position);
    let fine_fade = 1.0 - smoothstep(DETAIL_FADE_START, DETAIL_FADE_END, distance_to_eye);
    let strength = material.parameters[0].y * mix(CALM_STRENGTH, 1.0, patches)
        * (1.0 + clamp(speed / FAST_CURRENT, 0.0, 1.0));
    var water = surface;
    let slope = (broad + fine * FINE_WEIGHT * fine_fade) * strength;
    water.normal = normalize(vec3<f32>(slope.x, 1.0, slope.y));
    // Sparse moving foam; the built-in depth intersection foam is already in surface.base.
    let foam_pattern = flowing_foam(p, current, time, patches);
    let foam = smoothstep(FOAM_SPEED_START, FOAM_SPEED_FULL, speed)
        * smoothstep(FOAM_PATCH_START, FOAM_PATCH_FULL, foam_pattern)
        * material.parameters[0].z;
    water.base = vec4<f32>(mix(water.base.rgb, FOAM_COLOR, foam), max(water.base.a, foam));
    water.roughness = mix(water.roughness, FOAM_ROUGHNESS, foam);
    return standard_shade(water);
}
