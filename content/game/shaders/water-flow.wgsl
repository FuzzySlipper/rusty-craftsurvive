// The mesh UV carries horizontal current in metres/second. Surface texture coordinates are
// world anchored, as in Engine WATER. Vertex alpha is shore coverage for opacity and waves.
// The Engine still supplies depth colour and shore foam, using the displaced world position.
//
// The waves are CraftSurvive's one wave definition (#9902, Modules/Water/SeaWaves.cs): every wave
// constant below mirrors a C# one, and WaterRuleChecks holds them equal, so a boat or swimmer
// riding SeaState.Lift meets the surface drawn here. The product hands each update:
//   parameters[0] = frame origin x, z (world metres); wind velocity x, z (m/s over the ground)
//   parameters[1], [2] = the eight trains' phases now, measured from the frame origin
//   parameters[3] = the near water's bounds (world min x, min z, max x, max z)
// and each vertex carries its fetch in kilometres (normal.x) and its sea swell share (normal.z).
// Keywords pick a comparison: FLAT (flowing surface, no waves), VELOCITY (current colours),
// BASELINE (the Engine's water alone).
#import rusty::types::{Surface, Vertex}
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
const RIPPLE_STRENGTH: f32 = 0.24;
const FOAM_STRENGTH: f32 = 0.45;
const FLOW_CYCLE_SECONDS: f32 = 5.0;
// Whitecaps (visual only): they begin in a fresh breeze and cover the steep downwind faces in a gale.
const WHITECAP_WIND_START: f32 = 7.0;
const WHITECAP_WIND_FULL: f32 = 18.0;
const WHITECAP_FACE_START: f32 = 0.09;
const WHITECAP_FACE_FULL: f32 = 0.32;
const WHITECAP_PATCH_START: f32 = 0.47;
const WHITECAP_PATCH_FULL: f32 = 0.8;
const WHITECAP_STRENGTH: f32 = 0.9;
const TAU: f32 = 6.28318530718;
const METRES_PER_KILOMETRE: f32 = 1000.0;

// SeaWaves: the trains' wavelengths (metres) and bearings off the wind (radians).
const TRAIN_COUNT: i32 = 8;
const WAVELENGTHS: array<f32, 8> = array<f32, 8>(4.0, 5.6, 7.9, 11.0, 15.5, 22.0, 31.0, 43.0);
const ANGLES: array<f32, 8> = array<f32, 8>(0.35, -0.30, 0.18, -0.42, 0.26, -0.12, 0.08, -0.05);
const SWELL_TRAIN: i32 = 7;
const GRAVITY: f32 = 9.81;
const HEIGHT_GROWTH: f32 = 0.0016;
const PERIOD_GROWTH: f32 = 0.286;
const DEVELOPED_HEIGHT: f32 = 0.21;
const DEVELOPED_PERIOD: f32 = 7.16;
const MAXIMUM_HEIGHT: f32 = 1.0;
const SPECTRUM_WIDTH: f32 = 0.45;
const TAIL_POWER: f32 = 1.0;
const STEEPNESS: f32 = 0.12;
const CHOPPINESS: f32 = 0.9;
const SWELL_AMPLITUDE: f32 = 0.15;
const EDGE_FADE_METRES: f32 = 24.0;
const LEAST_FETCH: f32 = 1.0;

fn significant_height(wind: f32, fetch: f32) -> f32 {
    if wind <= 0.0 || fetch < LEAST_FETCH { return 0.0; }
    let limited = HEIGHT_GROWTH * wind * sqrt(fetch / GRAVITY);
    let developed = DEVELOPED_HEIGHT * wind * wind / GRAVITY;
    return min(MAXIMUM_HEIGHT, min(limited, developed));
}

fn peak_wavelength(wind: f32, fetch: f32) -> f32 {
    if wind <= 0.0 || fetch < LEAST_FETCH { return WAVELENGTHS[0]; }
    let limited = PERIOD_GROWTH * wind / GRAVITY * pow(GRAVITY * fetch / (wind * wind), 1.0 / 3.0);
    let period = min(limited, DEVELOPED_PERIOD * wind / GRAVITY);
    return GRAVITY * period * period / TAU;
}

fn train_phase(i: i32) -> f32 {
    if i < 4 { return material.parameters[1][i]; }
    return material.parameters[2][i - 4];
}

// The waves at a vertex: how far they move it, and the surface's slope there (the closed-form
// normal of the same sum, unnormalized), so the water is lit smoothly rather than per triangle.
struct Waves {
    offset: vec3<f32>,
    normal: vec3<f32>,
}

fn waves(vertex: Vertex) -> Waves {
    var result = Waves(vec3<f32>(0.0), vec3<f32>(0.0, 1.0, 0.0));
    let p = vertex.world_position.xz;
    let world_p = p + material.parameters[0].xy;
    let bounds = material.parameters[3];
    let edge = min(min(world_p.x - bounds.x, world_p.y - bounds.y), min(bounds.z - world_p.x, bounds.w - world_p.y));
    // Only the outer boundary fades: internal tile edges share the identical wave function.
    let share = vertex.color.a * smoothstep(0.0, EDGE_FADE_METRES, edge);
    if share <= 0.0 { return result; }
    let wind_velocity = material.parameters[0].zw;
    let speed = length(wind_velocity);
    let wind = wind_velocity / max(speed, 1e-6);
    let fetch = vertex.normal.x * METRES_PER_KILOMETRE;
    let height = significant_height(speed, fetch);
    let peak = peak_wavelength(speed, fetch);
    var wavelengths = WAVELENGTHS;
    var angles = ANGLES;
    var weights: array<f32, 8>;
    var sum = 0.0;
    for (var i = 0; i < TRAIN_COUNT; i++) {
        let ratio = wavelengths[i] / peak;
        let distance = log(ratio) / SPECTRUM_WIDTH;
        weights[i] = select(exp(-0.5 * distance * distance), pow(ratio, TAIL_POWER), ratio <= 1.0);
        sum += weights[i] * weights[i];
    }
    // Hs is four times the surface's standard deviation; a train of amplitude a has variance a^2/2.
    let scale = select(0.0, height / (2.0 * sqrt(2.0) * sqrt(sum)), sum > 0.0);
    for (var i = 0; i < TRAIN_COUNT; i++) {
        let k = TAU / wavelengths[i];
        var amplitude = min(weights[i] * scale, STEEPNESS / k);
        if i == SWELL_TRAIN { amplitude += SWELL_AMPLITUDE * vertex.normal.z; }
        amplitude *= share;
        let c = cos(angles[i]);
        let s = sin(angles[i]);
        let direction = vec2<f32>(wind.x * c - wind.y * s, wind.x * s + wind.y * c);
        let theta = k * dot(direction, p) + train_phase(i);
        let sideways = CHOPPINESS * amplitude * cos(theta);
        result.offset += vec3<f32>(direction.x * sideways, amplitude * sin(theta), direction.y * sideways);
        // A Gerstner train's normal: the height's slope along its direction, less the crest's lean.
        let slope = k * amplitude * cos(theta);
        result.normal -= vec3<f32>(direction.x * slope, CHOPPINESS * k * amplitude * sin(theta), direction.y * slope);
    }
    return result;
}

fn displace(vertex: Vertex) -> vec3<f32> {
#ifdef FLAT
    return vertex.world_position;
#else
#ifdef VELOCITY
    return vertex.world_position;
#else
#ifdef BASELINE
    return vertex.world_position;
#else
    return vertex.world_position + waves(vertex).offset;
#endif
#endif
#endif
}

// The mesh normal carries wave data (fetch, swell), so the water's own normal is up, or the waves'.
fn displace_normal(vertex: Vertex) -> vec3<f32> {
#ifdef FLAT
    return vec3<f32>(0.0, 1.0, 0.0);
#else
#ifdef VELOCITY
    return vec3<f32>(0.0, 1.0, 0.0);
#else
#ifdef BASELINE
    return vec3<f32>(0.0, 1.0, 0.0);
#else
    return waves(vertex).normal;
#endif
#endif
#endif
}

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
    let cycle = FLOW_CYCLE_SECONDS;
    let phase = fract(time / cycle + offset);
    let other = fract(phase + 0.5);
    let weight = 1.0 - abs(2.0 * phase - 1.0);
    return ripple((p - current * phase * cycle) / BROAD_METRES) * weight
        + ripple((p - current * other * cycle) / BROAD_METRES) * (1.0 - weight);
}

fn flowing_foam(p: vec2<f32>, current: vec2<f32>, time: f32, offset: f32) -> f32 {
    let cycle = FLOW_CYCLE_SECONDS;
    let phase = fract(time / cycle + offset);
    let other = fract(phase + 0.5);
    let weight = 1.0 - abs(2.0 * phase - 1.0);
    return noise((p - current * phase * cycle) / FINE_METRES) * weight
        + noise((p - current * other * cycle) / FINE_METRES) * (1.0 - weight);
}

fn shade(surface: Surface) -> vec4<f32> {
#ifdef BASELINE
    // The original material was opaque, including at the shore; keep that comparison intact. Its
    // normal is up (displace_normal).
    var original = surface;
    original.base.a = 1.0;
    return standard_shade(original);
#else
    let current = surface.uv;
    let speed = length(current);
#ifdef VELOCITY
    // Red = +X, green = +Z, blue = slow; brighter red/green means faster flow.
    return vec4<f32>(vec2<f32>(0.5) + clamp(current / FAST_CURRENT, vec2<f32>(-1.0), vec2<f32>(1.0)) * 0.5,
        1.0 - clamp(speed / FAST_CURRENT, 0.0, 1.0), 1.0);
#else
    let time = frame.time.x;
    let p = surface.world_position.xz + material.parameters[0].xy;
    let patches = noise(p / PATCH_METRES - WIND_DRIFT * time / PATCH_METRES);
    let warp = vec2<f32>(noise(p / PATCH_METRES), noise(p.yx / PATCH_METRES + WARP_OFFSET)) - 0.5;
    let broad = flowing_normal(p + warp * WARP_METRES, current + WIND_DRIFT, time, patches);
    // A rotated second scale; rotate its normal back into the water's world frame.
    let fine_p = vec2<f32>(p.y, -p.x) - FINE_DRIFT * time;
    let fine_sample = ripple(fine_p / FINE_METRES);
    let fine = vec2<f32>(-fine_sample.y, fine_sample.x);
    let distance_to_eye = distance(frame.camera.xyz, surface.world_position);
    let fine_fade = 1.0 - smoothstep(DETAIL_FADE_START, DETAIL_FADE_END, distance_to_eye);
    let strength = RIPPLE_STRENGTH * mix(CALM_STRENGTH, 1.0, patches)
        * (1.0 + clamp(speed / FAST_CURRENT, 0.0, 1.0));
    var water = surface;
    let slope = (broad + fine * FINE_WEIGHT * fine_fade) * strength;
    // The waves' own normal (displace_normal), interpolated smoothly across the one-metre triangles.
    let geometry_normal = normalize(surface.normal);
    water.normal = normalize(geometry_normal + vec3<f32>(slope.x, 0.0, slope.y));
    // Sparse moving foam; the built-in depth intersection foam is already in surface.base.
    let foam_pattern = flowing_foam(p, current, time, patches);
    var foam = smoothstep(FOAM_SPEED_START, FOAM_SPEED_FULL, speed)
        * smoothstep(FOAM_PATCH_START, FOAM_PATCH_FULL, foam_pattern)
        * FOAM_STRENGTH;
#ifndef FLAT
    // Whitecaps where the wind breaks the crests: on the faces the waves lean downwind, broken up.
    let wind_velocity = material.parameters[0].zw;
    let wind_speed = length(wind_velocity);
    let face = dot(geometry_normal.xz, wind_velocity / max(wind_speed, 1e-6));
    let caps = smoothstep(WHITECAP_WIND_START, WHITECAP_WIND_FULL, wind_speed)
        * smoothstep(WHITECAP_FACE_START, WHITECAP_FACE_FULL, face)
        * smoothstep(WHITECAP_PATCH_START, WHITECAP_PATCH_FULL, foam_pattern) * WHITECAP_STRENGTH;
    foam = max(foam, caps);
#endif
    water.base = vec4<f32>(mix(water.base.rgb, FOAM_COLOR, foam), max(water.base.a, foam));
    water.roughness = mix(water.roughness, FOAM_ROUGHNESS, foam);
    return standard_shade(water);
#endif
#endif
}
