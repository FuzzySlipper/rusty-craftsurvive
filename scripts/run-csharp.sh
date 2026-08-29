#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
engine_root="${RUSTY_ENGINE_ROOT:-$repo_root/../rusty-engine}"
runtime_manifest="$engine_root/Cargo.toml"
product_project="$repo_root/src/CraftSurvive.NativeProduct/CraftSurvive.NativeProduct.csproj"
product_library="$repo_root/src/CraftSurvive.NativeProduct/bin/Release/net10.0/linux-x64/publish/CraftSurvive.NativeProduct.so"
browser_bundle="$repo_root/src/ui/generated/product-bundle"
content_root="$repo_root/content"
port=0
bind_host="127.0.0.1"

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --port)
      port="${2:?--port requires a value}"
      shift 2
      ;;
    --bind-host)
      bind_host="${2:?--bind-host requires a value}"
      shift 2
      ;;
    *)
      printf '%s\n' "usage: $0 [--port <u16>] [--bind-host <ipv4>]" >&2
      exit 2
      ;;
  esac
done

if [[ ! -f "$runtime_manifest" ]]; then
  printf '%s\n' "Expected an adjacent rusty-engine checkout at: $engine_root" >&2
  exit 1
fi

node "$repo_root/scripts/generate-browser-bundle.mjs" "$browser_bundle"
dotnet publish "$product_project" --configuration Release --runtime linux-x64 --self-contained true
cargo run --manifest-path "$runtime_manifest" -p csharp-product-runtime -- \
  --library "$product_library" \
  --bundle-dir "$browser_bundle" \
  --content-dir "$content_root" \
  --mode realtime \
  --bind-host "$bind_host" \
  --port "$port"
