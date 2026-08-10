use std::{collections::BTreeSet, env, time::Instant};

use anyhow::{bail, Context, Result};
use rusty_craftsurvive::{
    initial_frame, replacement_frame, telemetry_frame, view_composition, DemoConfig, EditKind,
    GameWorld, PlayerController, PlayerInput,
};
use rusty_engine::{
    render_host_contracts::RendererPhysicalInputReadout,
    renderer_webview_host::{
        RendererWebviewAdapter, RendererWebviewBounds, RendererWebviewObservation,
        RendererWebviewOptions,
    },
};
use winit::{
    application::ApplicationHandler,
    event::WindowEvent,
    event_loop::{ActiveEventLoop, ControlFlow, EventLoop},
    window::{Window, WindowId},
};

struct CraftSurviveApplication {
    config: DemoConfig,
    world: GameWorld,
    player: PlayerController,
    window: Option<Window>,
    renderer: Option<RendererWebviewAdapter>,
    ready: bool,
    pending_input: Option<u64>,
    pressed_codes: BTreeSet<String>,
    pointer_buttons: u16,
    last_step: Instant,
    next_input_poll: Instant,
    dispose_request: Option<u64>,
    failure: Option<String>,
}

impl CraftSurviveApplication {
    fn new(config: DemoConfig) -> Result<Self> {
        Ok(Self {
            config,
            world: GameWorld::new(config.surface).map_err(anyhow::Error::msg)?,
            player: PlayerController::default(),
            window: None,
            renderer: None,
            ready: false,
            pending_input: None,
            pressed_codes: BTreeSet::new(),
            pointer_buttons: 0,
            last_step: Instant::now(),
            next_input_poll: Instant::now(),
            dispose_request: None,
            failure: None,
        })
    }

    fn mount(&mut self, event_loop: &ActiveEventLoop) -> Result<()> {
        let window = event_loop
            .create_window(
                Window::default_attributes()
                    .with_title(self.window_title())
                    .with_inner_size(winit::dpi::LogicalSize::new(1100, 720)),
            )
            .context("create CraftSurvive window")?;
        let renderer = RendererWebviewAdapter::mount(
            &window,
            RendererWebviewOptions {
                auto_start: true,
                bounds: window_bounds(&window),
                clear_color: Some(0x87_ceeb),
                pixel_ratio: window.scale_factor(),
                resources: Vec::new(),
            },
        )
        .map_err(|error| anyhow::anyhow!("mount Engine renderer: {error:?}"))?;
        self.window = Some(window);
        self.renderer = Some(renderer);
        Ok(())
    }

    fn initialize_renderer(&mut self) -> Result<()> {
        let renderer = self.renderer.as_mut().context("renderer unavailable")?;
        renderer.submit_frame(
            &initial_frame(self.world.presentation_mesh()).map_err(anyhow::Error::msg)?,
        )?;
        renderer.submit_presentation(
            &telemetry_frame(self.config.surface).map_err(anyhow::Error::msg)?,
        )?;
        renderer.configure_views(&view_composition(self.player.pose()))?;
        renderer.set_camera_pose(camera_pose(self.player.pose()), None)?;
        renderer.read_state()?;
        renderer.render_once(None)?;
        self.request_input()?;
        Ok(())
    }

    fn request_input(&mut self) -> Result<()> {
        if self.pending_input.is_none() {
            self.pending_input = Some(
                self.renderer
                    .as_mut()
                    .context("renderer unavailable")?
                    .read_physical_input()?,
            );
        }
        Ok(())
    }

    fn apply_input(&mut self, input: RendererPhysicalInputReadout) -> Result<()> {
        let now = Instant::now();
        let delta_seconds = now.saturating_duration_since(self.last_step).as_secs_f64();
        self.last_step = now;
        let pressed = input.pressed_codes.into_iter().collect::<BTreeSet<_>>();
        let axis = |positive: &str, negative: &str| {
            f64::from(pressed.contains(positive)) - f64::from(pressed.contains(negative))
        };
        self.player.step(
            self.world.scene(),
            PlayerInput {
                forward: axis("KeyW", "KeyS"),
                right: axis("KeyD", "KeyA"),
                vertical: axis("Space", "ShiftLeft"),
                yaw_delta_degrees: axis("ArrowRight", "ArrowLeft") * 90.0 * delta_seconds,
                pitch_delta_degrees: axis("ArrowDown", "ArrowUp") * 90.0 * delta_seconds,
            },
            delta_seconds,
        );

        let newly_pressed =
            |code: &str| pressed.contains(code) && !self.pressed_codes.contains(code);
        let destroy = (input.pointer.buttons & 1 != 0 && self.pointer_buttons & 1 == 0)
            || newly_pressed("KeyF");
        let place = (input.pointer.buttons & 2 != 0 && self.pointer_buttons & 2 == 0)
            || newly_pressed("KeyG");
        if destroy || place {
            let kind = if destroy {
                EditKind::Destroy
            } else {
                EditKind::Place { material_slot: 1 }
            };
            if let Some(receipt) = self
                .world
                .edit_from_view(
                    self.player.pose().position,
                    self.player.view_direction(),
                    kind,
                )
                .map_err(anyhow::Error::msg)?
            {
                self.renderer
                    .as_mut()
                    .context("renderer unavailable")?
                    .submit_frame(
                        &replacement_frame(self.world.presentation_mesh())
                            .map_err(anyhow::Error::msg)?,
                    )?;
                println!(
                    "CRAFTSURVIVE_EDIT kind={kind:?} voxel={:?} revision={} voxels={} authority_hash={}",
                    receipt.voxel, receipt.revision, receipt.voxel_count, receipt.authority_hash
                );
                if let Some(window) = &self.window {
                    window.set_title(&self.window_title());
                }
            }
        }
        self.renderer
            .as_mut()
            .context("renderer unavailable")?
            .set_camera_pose(camera_pose(self.player.pose()), None)?;
        self.pressed_codes = pressed;
        self.pointer_buttons = input.pointer.buttons;
        Ok(())
    }

    fn handle_observation(
        &mut self,
        observation: RendererWebviewObservation,
        event_loop: &ActiveEventLoop,
    ) -> Result<()> {
        match observation {
            RendererWebviewObservation::Ready(_) => {
                self.ready = true;
                self.initialize_renderer()?;
                println!(
                    "CRAFTSURVIVE_READY surface={} voxels={} authority_hash={}",
                    self.config.surface.as_str(),
                    self.world.scene().solid_voxel_count(),
                    self.world.scene().authority_hash()
                );
            }
            RendererWebviewObservation::PhysicalInputRead {
                request_id,
                readout,
            } if self.pending_input == Some(request_id) => {
                self.pending_input = None;
                self.apply_input(readout)?;
            }
            RendererWebviewObservation::FrameApplied { receipt, .. } if !receipt.applied => {
                bail!("renderer rejected terrain frame: {:?}", receipt.diagnostics);
            }
            RendererWebviewObservation::PresentationApplied { receipt, .. }
                if receipt.applied == 0 =>
            {
                bail!(
                    "renderer rejected HUD presentation: {:?}",
                    receipt.diagnostics
                );
            }
            RendererWebviewObservation::MountFailed { message } => {
                self.renderer = None;
                bail!("renderer mount failed transactionally: {message}");
            }
            RendererWebviewObservation::OperationFailed {
                request_id,
                operation,
                message,
            } => bail!("renderer {operation:?} request {request_id} failed: {message}"),
            RendererWebviewObservation::Disposed { request_id }
                if self.dispose_request == Some(request_id) =>
            {
                event_loop.exit();
            }
            _ => {}
        }
        Ok(())
    }

    fn window_title(&self) -> String {
        format!(
            "Rusty CraftSurvive — {} — revision {} — {} voxels",
            self.config.surface.as_str(),
            self.world.scene().source_revision().raw(),
            self.world.scene().solid_voxel_count()
        )
    }

    fn fail(&mut self, event_loop: &ActiveEventLoop, error: impl std::fmt::Display) {
        self.renderer = None;
        self.failure = Some(error.to_string());
        event_loop.exit();
    }
}

impl ApplicationHandler for CraftSurviveApplication {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.window.is_none() {
            if let Err(error) = self.mount(event_loop) {
                self.fail(event_loop, error);
            }
        }
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _window_id: WindowId,
        event: WindowEvent,
    ) {
        match event {
            WindowEvent::CloseRequested if self.dispose_request.is_none() => {
                match self.renderer.as_mut().map(RendererWebviewAdapter::dispose) {
                    Some(Ok(request_id)) => self.dispose_request = Some(request_id),
                    Some(Err(error)) => self.fail(event_loop, error),
                    None => event_loop.exit(),
                }
            }
            WindowEvent::Resized(_) if self.ready => {
                let result = (|| {
                    let window = self.window.as_ref().context("window unavailable")?;
                    self.renderer
                        .as_mut()
                        .context("renderer unavailable")?
                        .resize(window_bounds(window), window.scale_factor())?;
                    Result::<()>::Ok(())
                })();
                if let Err(error) = result {
                    self.fail(event_loop, error);
                }
            }
            _ => {}
        }
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        #[cfg(target_os = "linux")]
        while gtk::events_pending() {
            gtk::main_iteration_do(false);
        }
        let observations = self
            .renderer
            .as_mut()
            .map(RendererWebviewAdapter::drain_observations)
            .unwrap_or_default();
        for observation in observations {
            let result = observation
                .map_err(anyhow::Error::from)
                .and_then(|observation| self.handle_observation(observation, event_loop));
            if let Err(error) = result {
                self.fail(event_loop, error);
                return;
            }
        }
        if self.failure.is_none()
            && self.dispose_request.is_none()
            && self.ready
            && self.renderer.is_some()
            && Instant::now() >= self.next_input_poll
        {
            if let Err(error) = self.request_input() {
                self.fail(event_loop, error);
                return;
            }
            self.next_input_poll = Instant::now() + std::time::Duration::from_millis(16);
        }
    }
}

fn camera_pose(
    pose: rusty_craftsurvive::PlayerPose,
) -> rusty_engine::render_host_contracts::RendererCameraPose {
    rusty_engine::render_host_contracts::RendererCameraPose {
        position: pose.position,
        yaw_degrees: pose.yaw_degrees,
        pitch_degrees: pose.pitch_degrees,
    }
}

fn window_bounds(window: &Window) -> RendererWebviewBounds {
    let size = window.inner_size();
    let scale = window.scale_factor();
    RendererWebviewBounds {
        x: 0,
        y: 0,
        width: ((f64::from(size.width) / scale).round() as u32).max(1),
        height: ((f64::from(size.height) / scale).round() as u32).max(1),
    }
}

fn main() -> Result<()> {
    let config = DemoConfig::from_args(env::args().skip(1)).map_err(anyhow::Error::msg)?;
    let application = CraftSurviveApplication::new(config)?;
    if config.summary_only {
        println!(
            "CRAFTSURVIVE_SUMMARY surface={} voxels={} chunks={} vertices={} triangles={} authority_hash={}",
            config.surface.as_str(),
            application.world.scene().solid_voxel_count(),
            application.world.scene().resident_chunk_count(),
            application.world.presentation_mesh().stats.vertices,
            application.world.presentation_mesh().stats.triangles,
            application.world.scene().authority_hash(),
        );
        return Ok(());
    }
    #[cfg(target_os = "linux")]
    gtk::init().context("initialize GTK for Engine renderer host")?;
    let event_loop = EventLoop::new().context("create CraftSurvive event loop")?;
    event_loop.set_control_flow(ControlFlow::Poll);
    let mut application = application;
    event_loop
        .run_app(&mut application)
        .context("run CraftSurvive")?;
    if let Some(failure) = application.failure {
        bail!(failure);
    }
    Ok(())
}
