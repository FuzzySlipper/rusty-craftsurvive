// The glass storm's veil over the view (CameraView.SetImageEffect, #9740): the
// air ripples slowly and splits light into faint prismatic fringes, as much as
// effect_parameter(0).x (the weather's arcane channel, 0 to 1).
#import rusty::image::{ImagePixel, effect_parameter, picture_at}

const RIPPLE_UV: f32 = 0.0035;
const FRINGE_UV: f32 = 0.0025;

fn image_effect(pixel: ImagePixel) -> vec4<f32> {
    let arcane = effect_parameter(0u).x;
    let t = pixel.time;
    let ripple = vec2<f32>(sin(pixel.uv.y * 38.0 + t * 1.7), cos(pixel.uv.x * 31.0 - t * 1.3)) * RIPPLE_UV * arcane;
    let fringe = vec2<f32>(FRINGE_UV * arcane, 0.0);
    let at = pixel.uv + ripple;
    let red = picture_at(at + fringe).r;
    let green = picture_at(at).g;
    let blue = picture_at(at - fringe).b;
    return vec4<f32>(red, green, blue, pixel.color.a);
}
