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
    ClientMessage, GameSession, ServerMessage, SurfaceSelection, MAX_SESSION_MESSAGE_BYTES,
};
use serde::{Deserialize, Serialize};
use tokio::{net::TcpListener, sync::Mutex};

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
    web_root: PathBuf,
}

impl Options {
    fn parse(arguments: impl IntoIterator<Item = String>) -> Result<Self> {
        let mut host = "127.0.0.1".parse::<IpAddr>().expect("literal IP");
        let mut port = 4419_u16;
        let mut surface = SurfaceSelection::Box;
        let mut web_root = PathBuf::from("web/dist");
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
                "--web-root" => {
                    web_root =
                        PathBuf::from(arguments.next().context("--web-root requires a path")?);
                }
                _ => bail!("unknown argument '{argument}'"),
            }
        }
        Ok(Self {
            address: SocketAddr::new(host, port),
            surface,
            web_root,
        })
    }
}

#[derive(Clone)]
struct HostState {
    sessions: Arc<SessionPool>,
    default_surface: SurfaceSelection,
    web_root: Arc<PathBuf>,
}

struct SessionPool {
    box_surface: Arc<Mutex<GameSession>>,
    marching_cubes: Arc<Mutex<GameSession>>,
    dual_contouring: Arc<Mutex<GameSession>>,
}

impl SessionPool {
    fn new() -> Result<Self> {
        Ok(Self {
            box_surface: session(SurfaceSelection::Box)?,
            marching_cubes: session(SurfaceSelection::MarchingCubes)?,
            dual_contouring: session(SurfaceSelection::DualContouring)?,
        })
    }

    fn get(&self, surface: SurfaceSelection) -> Arc<Mutex<GameSession>> {
        match surface {
            SurfaceSelection::Box => Arc::clone(&self.box_surface),
            SurfaceSelection::MarchingCubes => Arc::clone(&self.marching_cubes),
            SurfaceSelection::DualContouring => Arc::clone(&self.dual_contouring),
        }
    }
}

fn session(surface: SurfaceSelection) -> Result<Arc<Mutex<GameSession>>> {
    Ok(Arc::new(Mutex::new(
        GameSession::new(surface).map_err(anyhow::Error::msg)?,
    )))
}

#[derive(Debug, Default, Deserialize)]
struct SessionQuery {
    surface: Option<String>,
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
    let session = state.sessions.get(surface);
    websocket
        .max_message_size(MAX_SESSION_MESSAGE_BYTES)
        .on_upgrade(move |socket| serve_session(socket, session))
}

async fn serve_session(mut socket: WebSocket, session: Arc<Mutex<GameSession>>) {
    let connected = {
        let mut session = session.lock().await;
        session.connect().map(|(readout, frame)| {
            let generation = readout.generation;
            (generation, ServerMessage::Welcome { readout, frame })
        })
    };
    let Ok((generation, welcome)) = connected else {
        let _ = socket.send(Message::Close(None)).await;
        return;
    };
    if send_json(&mut socket, &welcome).await.is_err() {
        let _ = session.lock().await.disconnect(generation);
        return;
    }

    while let Some(message) = socket.next().await {
        let response = match message {
            Ok(Message::Text(text)) => match serde_json::from_str::<ClientMessage>(text.as_str()) {
                Ok(message) => {
                    let mut session = session.lock().await;
                    match session.submit(message) {
                        Ok(update) => ServerMessage::Update { update },
                        Err(error) => ServerMessage::Rejected {
                            code: error.code(),
                            message: error.to_string(),
                            readout: session.readout(),
                        },
                    }
                }
                Err(error) => {
                    let session = session.lock().await;
                    ServerMessage::Rejected {
                        code: "malformedMessage",
                        message: error.to_string(),
                        readout: session.readout(),
                    }
                }
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
    let _ = session.lock().await.disconnect(generation);
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
        sessions: Arc::new(SessionPool::new()?),
        default_surface: options.surface,
        web_root: Arc::new(options.web_root),
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
        "CRAFTSURVIVE_BROWSER_READY address=http://{} surface={}",
        options.address,
        options.surface.as_str()
    );
    axum::serve(listener, application)
        .with_graceful_shutdown(async {
            let _ = tokio::signal::ctrl_c().await;
        })
        .await
        .context("serve CraftSurvive browser host")?;
    Ok(())
}
