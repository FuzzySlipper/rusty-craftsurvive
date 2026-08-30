#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
engine_root="${RUSTY_ENGINE_ROOT:-$repo_root/../rusty-engine}"
runtime_manifest="$engine_root/Cargo.toml"
product_project="$repo_root/src/CraftSurvive.NativeProduct/CraftSurvive.NativeProduct.csproj"
browser_bundle="$repo_root/src/ui/generated/product-bundle"
content_root="$repo_root/content"
persistence_root="$repo_root/.runtime/persistence"
port=0
bind_host="127.0.0.1"
loader="nativeaot"
live_debug=0

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
    --loader)
      loader="${2:?--loader requires nativeaot or coreclr}"
      shift 2
      ;;
    --live-debug)
      live_debug=1
      shift
      ;;
    *)
      printf '%s\n' "usage: $0 [--port <u16>] [--bind-host <ipv4>] [--loader <nativeaot|coreclr>] [--live-debug]" >&2
      exit 2
      ;;
  esac
done

if [[ ! -f "$runtime_manifest" ]]; then
  printf '%s\n' "Expected an adjacent rusty-engine checkout at: $engine_root" >&2
  exit 1
fi

node "$repo_root/scripts/generate-browser-bundle.mjs" "$browser_bundle"
runtime_args=(
  --bundle-dir "$browser_bundle"
  --content-dir "$content_root"
  --persistence-root "$persistence_root"
  --mode realtime
  --bind-host "$bind_host"
  --port "$port"
)

case "$loader" in
  nativeaot)
    product_library="$repo_root/src/CraftSurvive.NativeProduct/bin/Release/net10.0/linux-x64/publish/CraftSurvive.NativeProduct.so"
    dotnet publish "$product_project" --configuration Release --runtime linux-x64 --self-contained true
    runtime_args=(--library "$product_library" "${runtime_args[@]}")
    ;;
  coreclr)
    product_output="$repo_root/src/CraftSurvive.NativeProduct/bin/Debug/net10.0"
    dotnet build "$product_project" --configuration Debug -p:PublishAot=false -p:NativeLib=
    runtime_args=(
      --loader coreclr
      --library "$product_output/CraftSurvive.NativeProduct.dll"
      --runtimeconfig "$product_output/CraftSurvive.NativeProduct.runtimeconfig.json"
      "${runtime_args[@]}"
    )
    ;;
  *)
    printf '%s\n' "--loader must be nativeaot or coreclr" >&2
    exit 2
    ;;
esac

if [[ "$live_debug" == 1 ]]; then
  runtime_args+=(--live-debug)
fi

cargo run --manifest-path "$runtime_manifest" -p csharp-product-runtime --bin csharp-product-runtime -- "${runtime_args[@]}"
