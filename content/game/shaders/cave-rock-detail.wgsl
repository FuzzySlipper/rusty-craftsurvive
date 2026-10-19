// Cave rock detail (#10094): detail below the voxels' size, drawn on the smooth rock as light rather than geometry.
// Two layers bend the surface's normal before the standard shade lights it:
// - grain: the slope of a 3D value noise in world space, so the rock reads pitted and worn without a texture's repeat;
// - facets: the rock cut into small cells, each with one flat, tilted face, so light picks out stepped micro-facets
//   (the micro-voxel read) while the silhouette stays the smooth voxel surface.
// Only the normal changes: collision, shadows and silhouettes stay the voxels'.
//
// material.parameters[0]: x grain strength (0 none), y grain frequency (cycles per metre), z facet size (metres,
// 0 none), w facet strength (how far each facet tilts from the surface, 0 to 1).
// material.parameters[1]: x 1 to colour the rock from the cave's palette instead of its painted texture (no drawn
// forms: the forms are the geometry's and the grain's), y what to add to a world height for the layout's height,
// zw the beds' dip along x and z (KarstCave.BedDip), so the strata bands follow the beds the water followed.
//
// material.parameters[2]: x the size of a colour cell (metres, 0 none): the palette read once per cell, so each
// cell is one flat colour (with facets of the same size, each facet a lit 3D pixel); y how many steps the facets'
// normals snap to (0 for FACET_STEPS; fewer is flatter and chunkier); z what cells do as they shrink toward a screen
// pixel: 0 nothing, 1 fade (to the smooth surface and colour), 2 grow (doubling, so a cell stays a few pixels across);
// w how much neighbouring facets lean the same way (0 each its own, 1 a smooth swell), so a broad face steps in
// terraces rather than a checker.
//
// Cells lie on the surface, not in world cubes: each is a square of the plane the surface most faces (x, y or z),
// so a wall shows square cells instead of the ragged cuts of cubes through it.
//
// material.parameters[3]: the rock's age class (#10094), one of: x patina (levels dry for ages: darker, warmer, stained,
// a pale calcite crust on what faces down), y washed (the active level: cleaner, cooler, wetter and glossier, its facets
// softened by the water), z sediment (settled silt and mud: its own colour, fine and flat).
//
// The palette is colour only, at sizes no form is drawn at: bands along the beds, broad mottling and mineral
// staining, a texel-fine speckle, and floors (where sediment settled) and the still-flooded depths darker and muddier.

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

// A cell is this many screen pixels across at least before it grows (grow), or by the time it has faded (fade).
const PIXELS_PER_CELL: f32 = 3.0;
// The coherent part of a facet's lean varies over this many cells.
const LEAN_CELLS: f32 = 5.0;

// Which plane a surface most faces: 0 is x (the yz plane), 1 y, 2 z.
fn facing(n: vec3<f32>) -> i32 {
    let a = abs(n);
    if (a.x >= a.y && a.x >= a.z) { return 0; }
    if (a.y >= a.z) { return 1; }
    return 2;
}

fn in_plane(p: vec3<f32>, axis: i32) -> vec2<f32> {
    if (axis == 0) { return p.yz; }
    if (axis == 1) { return p.xz; }
    return p.xy;
}

// A point's cell on the plane its surface faces: the cell's index (with the plane, so cells of different planes
// differ) and the cell's centre in the world, at the point's own depth.
fn cell_index(p: vec3<f32>, axis: i32, size: f32) -> vec3<i32> {
    let c = floor(in_plane(p, axis) / size);
    return vec3<i32>(i32(c.x), i32(c.y), axis * 104729);
}

fn cell_centre(p: vec3<f32>, axis: i32, size: f32) -> vec3<f32> {
    let c = (floor(in_plane(p, axis) / size) + vec2<f32>(0.5)) * size;
    if (axis == 0) { return vec3<f32>(p.x, c.x, c.y); }
    if (axis == 1) { return vec3<f32>(c.x, p.y, c.y); }
    return vec3<f32>(c.x, c.y, p.z);
}

// A cell's size where a screen pixel covers `footprint` metres: unchanged, or doubled until it spans a few pixels.
fn cell_size(size: f32, footprint: f32, mode: f32) -> f32 {
    if (mode < 1.5) { return size; }
    return size * exp2(max(0.0, ceil(log2(footprint * PIXELS_PER_CELL / size))));
}

// How much of a cell's effect is kept: all of it, or fading out as the cell shrinks toward a pixel.
fn cell_keep(size: f32, footprint: f32, mode: f32) -> f32 {
    if (mode < 0.5 || mode > 1.5) { return 1.0; }
    return 1.0 - smoothstep(size / PIXELS_PER_CELL, size, footprint * PIXELS_PER_CELL);
}

// The surface as facets: the normal snapped to coarse steps, then leant by a direction fixed per cell, part its own
// and part a swell shared with its neighbours.
fn faceted(n: vec3<f32>, p: vec3<f32>, size: f32, strength: f32, steps: f32, coherence: f32) -> vec3<f32> {
    let axis = facing(n);
    let cell = cell_index(p, axis, size);
    let centre = cell_centre(p, axis, size);
    let snapped = normalize(round(n * steps) / steps + n * 0.001);
    let own = vec3<f32>(hash3(cell), hash3(cell + vec3<i32>(71, 13, 5)), hash3(cell + vec3<i32>(9, 57, 113)));
    let q = centre / (size * LEAN_CELLS);
    let swell = vec3<f32>(value_noise(q), value_noise(q + vec3<f32>(19.0, 7.0, 3.0)), value_noise(q + vec3<f32>(5.0, 31.0, 11.0)));
    let tilt = mix(own, swell, coherence) * 2.0 - 1.0;
    let sideways = tilt - dot(tilt, snapped) * snapped;
    return normalize(snapped + strength * sideways);
}

// The palette (linear albedo), near the painted cave rock's own mean (0.065) so the light reads the same.
const LIMESTONE: vec3<f32> = vec3<f32>(0.088, 0.075, 0.058);
const GREY_BED: vec3<f32> = vec3<f32>(0.060, 0.061, 0.060);
const PARTING: vec3<f32> = vec3<f32>(0.032, 0.029, 0.027);
const OCHRE: vec3<f32> = vec3<f32>(0.100, 0.062, 0.030);
const SEDIMENT: vec3<f32> = vec3<f32>(0.048, 0.037, 0.026);
// Beds a few metres thick; partings show as thin dark lines between some of them.
const BED_FREQUENCY: f32 = 0.22;
const PARTING_SHARE: f32 = 0.08;
const MOTTLE_FREQUENCY: f32 = 0.35;
const STAIN_FREQUENCY: f32 = 0.12;
// The speckle's cell (metres): finer than any form, so it reads as the stone's grain of colour.
const SPECKLE: f32 = 0.04;
// Below this layout height the cave still stands in water's reach: wetter, muddier, glossier.
const WET_BELOW: f32 = 17.0;
// The age classes: patina darkens and warms the rock and crusts what faces down with calcite; washing lightens and
// cools it and polishes it; sediment is mud.
const PATINA_TINT: vec3<f32> = vec3<f32>(0.78, 0.66, 0.5);
const CALCITE: vec3<f32> = vec3<f32>(0.16, 0.15, 0.13);
const WASHED_TINT: vec3<f32> = vec3<f32>(1.12, 1.12, 1.15);
const MUD: vec3<f32> = vec3<f32>(0.05, 0.038, 0.026);
const WASHED_ROUGHNESS: f32 = 0.45;
const SEDIMENT_FACETS: f32 = 0.3;

fn aged(colour: vec3<f32>, p: vec3<f32>, n: vec3<f32>, age: vec4<f32>) -> vec3<f32> {
    var c = colour;
    if (age.x > 0.0) {
        let stain = value_noise(p * 0.5 + vec3<f32>(3.0, 9.0, 1.0));
        c = c * mix(vec3<f32>(1.0), PATINA_TINT, 0.6 + 0.4 * stain);
        // Calcite crust where water once dripped and dried: on ceilings and what hangs.
        c = mix(c, CALCITE, smoothstep(-0.3, -0.8, n.y) * (0.5 + 0.5 * value_noise(p * 1.3)));
    }
    if (age.y > 0.0) {
        // Washed: the mottling evened out, lighter and cooler.
        c = mix(c, vec3<f32>(dot(c, vec3<f32>(0.33))), 0.35) * WASHED_TINT;
    }
    if (age.z > 0.0) {
        c = MUD * (0.85 + 0.3 * value_noise(p * 2.0));
    }
    return c;
}

fn palette(p: vec3<f32>, n: vec3<f32>, frame: vec4<f32>) -> vec3<f32> {
    let height = p.y + frame.y;
    let bed = height - frame.z * p.x - frame.w * p.z;
    let band = value_noise(vec3<f32>(bed * BED_FREQUENCY, 0.5, 0.5));
    var colour = mix(GREY_BED, LIMESTONE, smoothstep(0.3, 0.7, band));
    // A parting where the band's own phase is near a whole bed.
    let phase = fract(bed * BED_FREQUENCY * 2.0);
    colour = mix(colour, PARTING, (1.0 - smoothstep(0.0, PARTING_SHARE, min(phase, 1.0 - phase))) * step(0.5, hash3(vec3<i32>(i32(floor(bed * BED_FREQUENCY * 2.0)), 3, 7))));
    let mottle = value_noise(p * MOTTLE_FREQUENCY);
    colour = colour * (0.85 + 0.3 * mottle);
    colour = mix(colour, OCHRE, 0.45 * smoothstep(0.6, 0.85, value_noise(p * STAIN_FREQUENCY + vec3<f32>(31.0, 7.0, 11.0))));
    let speckle = hash3(vec3<i32>(floor(p / SPECKLE)));
    colour = colour * (0.94 + 0.12 * speckle);
    // Sediment on what faces up, and the wet depths.
    colour = mix(colour, SEDIMENT, 0.6 * smoothstep(0.55, 0.9, n.y));
    let wet = 1.0 - smoothstep(WET_BELOW - 3.0, WET_BELOW + 1.0, height);
    return colour * (1.0 - 0.35 * wet);
}

fn shade(surface: Surface) -> vec4<f32> {
    let detail = material.parameters[0];
    let colouring = material.parameters[1];
    let pixels = material.parameters[2];
    var s = surface;
    // How many metres one screen pixel covers here, for the cells' fade or growth.
    let footprint = max(length(dpdx(s.world_position)), length(dpdy(s.world_position)));
    let axis = facing(s.normal);
    if (colouring.x > 0.0) {
        // With colour cells, the palette is read at the cell's centre, so the cell is one flat colour.
        var colour = palette(s.world_position, s.normal, colouring);
        if (pixels.x > 0.0) {
            let size = cell_size(pixels.x, footprint, pixels.z);
            let cell_colour = palette(cell_centre(s.world_position, axis, size), s.normal, colouring);
            colour = mix(colour, cell_colour, cell_keep(size, footprint, pixels.z));
        }
        s.base = vec4<f32>(aged(colour, s.world_position, s.normal, material.parameters[3]), s.base.a);
        if (material.parameters[3].y > 0.0) {
            s.roughness = min(s.roughness, WASHED_ROUGHNESS);
        }
        let height = s.world_position.y + colouring.y;
        s.roughness = mix(s.roughness, 0.5, 1.0 - smoothstep(WET_BELOW - 3.0, WET_BELOW + 1.0, height));
    }
    if (detail.z > 0.0) {
        let steps = select(FACET_STEPS, pixels.y, pixels.y > 0.0);
        let size = cell_size(detail.z, footprint, pixels.z);
        // Sediment lies flat: its facets barely lean.
        let lean = select(1.0, SEDIMENT_FACETS, material.parameters[3].z > 0.0);
        s.normal = faceted(s.normal, s.world_position, size, detail.w * lean * cell_keep(size, footprint, pixels.z), steps, pixels.w);
    }
    if (detail.x > 0.0) {
        s.normal = grained(s.normal, s.world_position, detail.x, detail.y);
    }
    return standard_shade(s);
}
