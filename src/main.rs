use std::{collections::BTreeSet, env, time::Instant};

use anyhow::{bail, Context, Result};
use rusty_craftsurvive::{
    platform_frame, telemetry_frame, terrain_texture_resource, DemoConfig, EditKind, EditOutcome,
    GameWorld, PlayerController, PlayerInput, TerrainProjector,
};
use rusty_engine::{
    render_host_contracts::RendererPhysicalInputReadout,
    renderer_webview_host::{
        RendererResource, RendererWebviewAdapter, RendererWebviewBounds,
        RendererWebviewObservation, RendererWebviewOptions,
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
    terrain_projector: TerrainProjector,
    player: PlayerController,
    window: Option<Window>,
    renderer: Option<RendererWebviewAdapter>,
    ready: bool,
    pending_input: Option<u64>,
    pressed_codes: BTreeSet<String>,
    pointer_buttons: u16,
    brush_radius: u8,
    last_step: Instant,
    next_input_poll: Instant,
    dispose_request: Option<u64>,
    failure: Option<String>,
}

impl CraftSurviveApplication {
    fn new(config: DemoConfig) -> Result<Self> {
        Ok(Self {
            config,
            world: GameWorld::with_terrain(config.surface, config.terrain)
                .map_err(anyhow::Error::msg)?,
            terrain_projector: TerrainProjector::new(),
            player: PlayerController::default(),
            window: None,
            renderer: None,
            ready: false,
            pending_input: None,
            pressed_codes: BTreeSet::new(),
            pointer_buttons: 0,
            brush_radius: 0,
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
        let terrain_texture = terrain_texture_resource().map_err(anyhow::Error::msg)?;
        let renderer = RendererWebviewAdapter::mount(
            &window,
            RendererWebviewOptions {
                auto_start: true,
                bounds: window_bounds(&window),
                clear_color: Some(0x87_ceeb),
                pixel_ratio: window.scale_factor(),
                resources: vec![RendererResource {
                    identity: terrain_texture.identity,
                    content_hash: terrain_texture.content_hash,
                    media_type: terrain_texture.media_type.to_owned(),
                    bytes: terrain_texture.bytes.to_vec(),
                }],
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
            &self
                .terrain_projector
                .project(self.world.scene(), self.player.platform_position(), true)
                .map_err(anyhow::Error::msg)?,
        )?;
        renderer.submit_presentation(
            &telemetry_frame(self.config.surface).map_err(anyhow::Error::msg)?,
        )?;
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
        self.player
            .step(
                self.world.scene(),
                PlayerInput {
                    forward: axis("KeyW", "KeyS"),
                    right: axis("KeyD", "KeyA"),
                    jump: pressed.contains("Space"),
                    crouch: pressed.contains("ControlLeft") || pressed.contains("ControlRight"),
                    sprint: pressed.contains("ShiftLeft") || pressed.contains("ShiftRight"),
                    impulse: pressed.contains("KeyH"),
                    yaw_delta_degrees: axis("ArrowRight", "ArrowLeft") * 90.0 * delta_seconds,
                    pitch_delta_degrees: axis("ArrowDown", "ArrowUp") * 90.0 * delta_seconds,
                },
                delta_seconds,
            )
            .map_err(anyhow::Error::msg)?;
        self.renderer
            .as_mut()
            .context("renderer unavailable")?
            .submit_frame(
                &platform_frame(self.player.platform_position()).map_err(anyhow::Error::msg)?,
            )?;

        let newly_pressed =
            |code: &str| pressed.contains(code) && !self.pressed_codes.contains(code);
        for (code, radius) in [("Digit1", 0), ("Digit2", 1), ("Digit3", 2)] {
            if newly_pressed(code) {
                self.brush_radius = radius;
                if let Some(window) = &self.window {
                    window.set_title(&self.window_title());
                }
            }
        }
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
            match self
                .world
                .edit_from_view(
                    self.player.pose().position,
                    self.player.view_direction(),
                    kind,
                    self.brush_radius,
                    &self.player,
                )
                .map_err(anyhow::Error::msg)?
            {
                EditOutcome::Applied(receipt) => {
                    let frame = self
                        .terrain_projector
                        .project(self.world.scene(), self.player.platform_position(), false)
                        .map_err(anyhow::Error::msg)?;
                    self.renderer
                        .as_mut()
                        .context("renderer unavailable")?
                        .submit_frame(&frame)?;
                    println!(
                        "CRAFTSURVIVE_EDIT kind={kind:?} voxel={:?} brush={} affected={} revision={} voxels={} dirty_chunks={} rebuilt_chunks={} reused_chunks={} removed_chunks={} frame_ops={} mesh_build_ms={:.3} edit_ms={:.3} authority_hash={}",
                        receipt.voxel,
                        self.brush_radius,
                        receipt.affected_voxels,
                        receipt.revision,
                        receipt.voxel_count,
                        receipt.dirty_chunks,
                        receipt.rebuilt_chunks,
                        receipt.reused_chunks,
                        receipt.removed_chunks,
                        frame.ops.len(),
                        receipt.mesh_build_ms,
                        receipt.edit_ms,
                        receipt.authority_hash
                    );
                    if let Some(window) = &self.window {
                        window.set_title(&self.window_title());
                    }
                }
                EditOutcome::Rejected(rejection) => println!(
                    "CRAFTSURVIVE_EDIT_REJECTED kind={kind:?} code={} rejection={rejection:?}",
                    rejection.code()
                ),
                EditOutcome::Miss => {}
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
            "Rusty CraftSurvive — {} — seed 0x{:016x} — {}x{} — brush {} — revision {} — {} voxels",
            self.config.surface.as_str(),
            self.config.terrain.seed,
            self.config.terrain.size,
            self.config.terrain.size,
            self.brush_radius,
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
        let mesh = application.world.mesh_stats();
        println!(
            "CRAFTSURVIVE_SUMMARY surface={} seed=0x{:016x} size={} voxels={} chunks={} vertices={} triangles={} generation_ms={:.3} authority_build_ms={:.3} mesh_build_ms={:.3} authority_hash={}",
            config.surface.as_str(),
            config.terrain.seed,
            config.terrain.size,
            application.world.scene().solid_voxel_count(),
            mesh.chunks,
            mesh.vertices,
            mesh.triangles,
            application.world.metrics().generation_ms,
            application.world.metrics().authority_build_ms,
            application.world.metrics().mesh_build_ms,
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
