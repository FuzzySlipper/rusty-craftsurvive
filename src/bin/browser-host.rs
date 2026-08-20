use std::{
    env,
    net::{IpAddr, SocketAddr},
    path::{Path, PathBuf},
    sync::Arc,
};

use anyhow::{bail, Context, Result};
use axum::{
    body::Body,
    extract::{
        ws::{Message, WebSocket, WebSocketUpgrade},
        Query, State,
    },
    http::{header, HeaderValue, StatusCode, Uri},
    response::{IntoResponse, Response},
    routing::get,
    Json, Router,
};
use futures_util::StreamExt;
use rusty_craftsurvive::{
    parse_seed, session_resources, ClientMessage, GameSession, ServerMessage, SpawnSelection,
    SurfaceSelection, TerrainConfig, MAX_SESSION_MESSAGE_BYTES,
};
use serde::{Deserialize, Serialize};
use tokio::net::TcpListener;

const PROJECT_ID: &str = "rusty-craftsurvive";
const FALLBACK_SHELL: &str = r#"<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>Rusty CraftSurvive host</title></head><body>
<main><h1>Rusty CraftSurvive host is ready</h1>
<p>The Rust gameplay session is available at <code>/api/session</code>.</p>
<p>The minimum playable browser shell is supplied by task 6773.</p></main>
</body></html>"#;

#[derive(Debug, Clone)]
struct Options {
    address: SocketAddr,
    surface: SurfaceSelection,
    terrain: TerrainConfig,
    web_root: PathBuf,
    save_root: PathBuf,
}

impl Options {
    fn parse(arguments: impl IntoIterator<Item = String>) -> Result<Self> {
        let mut host = "127.0.0.1".parse::<IpAddr>().expect("literal IP");
        let mut port = 4419_u16;
        let mut surface = SurfaceSelection::Box;
        let mut terrain = TerrainConfig::default();
        let mut web_root = PathBuf::from("web/dist");
        let mut save_root = PathBuf::from("target/craftsurvive-saves");
        let mut arguments = arguments.into_iter();
        while let Some(argument) = arguments.next() {
            match argument.as_str() {
                "--host" => {
                    host = arguments
                        .next()
                        .context("--host requires an IP address")?
                        .parse()
                        .context("parse --host IP address")?;
                }
                "--port" => {
                    port = arguments
                        .next()
                        .context("--port requires a number")?
                        .parse()
                        .context("parse --port")?;
                }
                "--surface" => {
                    surface = arguments
                        .next()
                        .context("--surface requires box, mc, or dc")?
                        .parse()
                        .map_err(anyhow::Error::msg)?;
                }
                "--seed" => {
                    terrain.seed = parse_seed(
                        &arguments
                            .next()
                            .context("--seed requires an unsigned integer")?,
                    )
                    .map_err(anyhow::Error::msg)?;
                }
                "--size" => {
                    terrain.size = arguments
                        .next()
                        .context("--size requires an even integer")?
                        .parse()
                        .context("parse --size")?;
                }
                "--web-root" => {
                    web_root =
                        PathBuf::from(arguments.next().context("--web-root requires a path")?);
                }
                "--save-root" => {
                    save_root =
                        PathBuf::from(arguments.next().context("--save-root requires a path")?);
                }
                _ => bail!("unknown argument '{argument}'"),
            }
        }
        terrain = TerrainConfig::new(terrain.seed, terrain.size).map_err(anyhow::Error::msg)?;
        Ok(Self {
            address: SocketAddr::new(host, port),
            surface,
            terrain,
            web_root,
            save_root,
        })
    }
}

#[derive(Clone)]
struct HostState {
    default_surface: SurfaceSelection,
    default_terrain: TerrainConfig,
    web_root: PathBuf,
    save_root: PathBuf,
    save_lock: Arc<tokio::sync::Mutex<()>>,
}

#[derive(Debug, Default, Deserialize)]
struct SessionQuery {
    surface: Option<String>,
    seed: Option<String>,
    size: Option<u16>,
    course: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct HealthReadout {
    project: &'static str,
    status: &'static str,
    session_protocol_version: u32,
}

async fn health() -> impl IntoResponse {
    let mut response = Json(HealthReadout {
        project: PROJECT_ID,
        status: "ok",
        session_protocol_version: rusty_craftsurvive::SESSION_PROTOCOL_VERSION,
    })
    .into_response();
    response
        .headers_mut()
        .insert("x-den-project", HeaderValue::from_static(PROJECT_ID));
    response
}

async fn session_upgrade(
    websocket: WebSocketUpgrade,
    State(state): State<HostState>,
    Query(query): Query<SessionQuery>,
) -> Response {
    let surface = match query.surface {
        Some(surface) => match surface.parse() {
            Ok(surface) => surface,
            Err(message) => return (StatusCode::BAD_REQUEST, message).into_response(),
        },
        None => state.default_surface,
    };
    let seed = match query.seed {
        Some(seed) => match parse_seed(&seed) {
            Ok(seed) => seed,
            Err(message) => return (StatusCode::BAD_REQUEST, message).into_response(),
        },
        None => state.default_terrain.seed,
    };
    let terrain = match TerrainConfig::new(seed, query.size.unwrap_or(state.default_terrain.size)) {
        Ok(terrain) => terrain,
        Err(message) => return (StatusCode::BAD_REQUEST, message).into_response(),
    };
    let spawn = match query.course.as_deref() {
        None | Some("route") => SpawnSelection::Route,
        Some("garden") | Some("ghost-plate") => SpawnSelection::DepthSplatGarden,
        Some("platform") => SpawnSelection::MovingPlatform,
        Some("stream") => SpawnSelection::StreamingWest,
        Some("far") => SpawnSelection::FarPositive,
        Some("far-negative") => SpawnSelection::FarNegative,
        Some(value) => {
            return (
                StatusCode::BAD_REQUEST,
                format!("unsupported controller course '{value}'"),
            )
                .into_response()
        }
    };
    let mut session = match GameSession::with_terrain_and_spawn(surface, terrain, spawn) {
        Ok(session) => session,
        Err(message) => return (StatusCode::INTERNAL_SERVER_ERROR, message).into_response(),
    };
    let save_path = terrain_save_path(&state.save_root, seed);
    match tokio::fs::read(&save_path).await {
        Ok(bytes) => {
            if let Err(message) = session.load_overlay_bytes(&bytes) {
                return (
                    StatusCode::CONFLICT,
                    format!("terrain overlay load failed: {message}"),
                )
                    .into_response();
            }
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => {
            return (
                StatusCode::INTERNAL_SERVER_ERROR,
                format!("read terrain overlay: {error}"),
            )
                .into_response()
        }
    }
    let save_lock = state.save_lock.clone();
    websocket
        .max_message_size(MAX_SESSION_MESSAGE_BYTES)
        .on_upgrade(move |socket| serve_session(socket, session, save_path, save_lock))
}

async fn serve_session(
    mut socket: WebSocket,
    mut session: GameSession,
    save_path: PathBuf,
    save_lock: Arc<tokio::sync::Mutex<()>>,
) {
    let connected = session.connect().map(|(readout, frame)| {
        let generation = readout.generation;
        session_resources().map(|resources| {
            (
                generation,
                ServerMessage::Welcome {
                    readout,
                    frame,
                    resources,
                },
            )
        })
    });
    let Ok(Ok((generation, welcome))) = connected else {
        let _ = socket.send(Message::Close(None)).await;
        return;
    };
    if send_json(&mut socket, &welcome).await.is_err() {
        let _ = session.disconnect(generation);
        return;
    }

    while let Some(message) = socket.next().await {
        let response = match message {
            Ok(Message::Text(text)) => match serde_json::from_str::<ClientMessage>(text.as_str()) {
                Ok(message) => match session.submit(message) {
                    Ok(update) => {
                        if update.edit.is_some() {
                            match session.overlay_bytes() {
                                Ok(bytes) => {
                                    let _guard = save_lock.lock().await;
                                    if let Err(error) =
                                        write_overlay_atomic(&save_path, &bytes).await
                                    {
                                        eprintln!(
                                            "CRAFTSURVIVE_SAVE_FAILED path={} error={error}",
                                            save_path.display()
                                        );
                                        let _ = socket.send(Message::Close(None)).await;
                                        break;
                                    }
                                }
                                Err(error) => {
                                    eprintln!("CRAFTSURVIVE_SAVE_FAILED encode={error}");
                                    let _ = socket.send(Message::Close(None)).await;
                                    break;
                                }
                            }
                        }
                        ServerMessage::Update {
                            update: Box::new(update),
                        }
                    }
                    Err(error) => ServerMessage::Rejected {
                        code: error.code(),
                        message: error.to_string(),
                        readout: session.readout(),
                    },
                },
                Err(error) => ServerMessage::Rejected {
                    code: "malformedMessage",
                    message: error.to_string(),
                    readout: session.readout(),
                },
            },
            Ok(Message::Ping(bytes)) => {
                if socket.send(Message::Pong(bytes)).await.is_err() {
                    break;
                }
                continue;
            }
            Ok(Message::Close(_)) | Err(_) => break,
            _ => continue,
        };
        if send_json(&mut socket, &response).await.is_err() {
            break;
        }
    }
    let _ = session.disconnect(generation);
}

fn terrain_save_path(root: &Path, seed: u64) -> PathBuf {
    root.join(format!("terrain-v2-{seed:016x}.json"))
}

async fn write_overlay_atomic(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    tokio::fs::create_dir_all(parent).await?;
    let temporary = path.with_extension("json.tmp");
    tokio::fs::write(&temporary, bytes).await?;
    tokio::fs::rename(temporary, path).await
}

async fn send_json(socket: &mut WebSocket, message: &ServerMessage) -> Result<(), axum::Error> {
    let encoded = serde_json::to_string(message).expect("server message is serializable");
    socket.send(Message::Text(encoded.into())).await
}

async fn static_asset(State(state): State<HostState>, uri: Uri) -> Response {
    let requested = uri.path().trim_start_matches('/');
    if requested.split('/').any(|part| matches!(part, ".." | ".")) || requested.contains('\\') {
        return StatusCode::BAD_REQUEST.into_response();
    }
    let requested = if requested.is_empty() {
        "index.html"
    } else {
        requested
    };
    let mut path = state.web_root.join(requested);
    let mut bytes = read_asset(&path).await;
    if bytes.is_none() && Path::new(requested).extension().is_none() {
        path = state.web_root.join("index.html");
        bytes = read_asset(&path).await;
    }
    let Some(bytes) = bytes else {
        if requested == "index.html" {
            return html_response(FALLBACK_SHELL.as_bytes().to_vec());
        }
        return StatusCode::NOT_FOUND.into_response();
    };
    let content_type = match path.extension().and_then(|value| value.to_str()) {
        Some("html") => "text/html; charset=utf-8",
        Some("js") => "text/javascript; charset=utf-8",
        Some("css") => "text/css; charset=utf-8",
        Some("json") => "application/json",
        Some("svg") => "image/svg+xml",
        Some("png") => "image/png",
        _ => "application/octet-stream",
    };
    Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, content_type)
        .header(header::CACHE_CONTROL, "no-store")
        .body(Body::from(bytes))
        .expect("static response")
}

async fn read_asset(path: &Path) -> Option<Vec<u8>> {
    tokio::fs::read(path).await.ok()
}

fn html_response(bytes: Vec<u8>) -> Response {
    Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, "text/html; charset=utf-8")
        .header(header::CACHE_CONTROL, "no-store")
        .body(Body::from(bytes))
        .expect("HTML response")
}

#[tokio::main]
async fn main() -> Result<()> {
    let options = Options::parse(env::args().skip(1))?;
    let state = HostState {
        default_surface: options.surface,
        default_terrain: options.terrain,
        web_root: options.web_root,
        save_root: options.save_root,
        save_lock: Arc::new(tokio::sync::Mutex::new(())),
    };
    let application = Router::new()
        .route("/health", get(health))
        .route("/api/session", get(session_upgrade))
        .fallback(get(static_asset))
        .with_state(state);
    let listener = TcpListener::bind(options.address)
        .await
        .with_context(|| format!("bind CraftSurvive browser host at {}", options.address))?;
    println!(
        "CRAFTSURVIVE_BROWSER_READY address=http://{} surface={} seed=0x{:016x} size={}",
        options.address,
        options.surface.as_str(),
        options.terrain.seed,
        options.terrain.size,
    );
    axum::serve(listener, application)
        .with_graceful_shutdown(async {
            let _ = tokio::signal::ctrl_c().await;
        })
        .await
        .context("serve CraftSurvive browser host")?;
    Ok(())
}
