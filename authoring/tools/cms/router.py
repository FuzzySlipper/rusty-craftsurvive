"""Minimal stdlib HTTP route table with named path parameters and structured JSON errors."""

from dataclasses import dataclass, field
from http import HTTPStatus
import json
import re
from urllib.parse import parse_qs, unquote, urlsplit



METHODS = ("GET", "POST", "PUT", "DELETE")
PARAM_PATTERN = re.compile(r"\{([a-zA-Z_][a-zA-Z0-9_]*)(?::(path))?\}")


class ApiError(Exception):
    """HTTP error raised by route handlers to produce a structured JSON response."""

    def __init__(self, status: HTTPStatus, message: str, *, details: dict | None = None):
        super().__init__(message)
        self.status = HTTPStatus(status)
        self.message = message
        self.details = details or {}

    def payload(self):
        return {"error": self.message} | self.details


class BadRequestError(ApiError):
    def __init__(self, message: str, *, details: dict | None = None):
        super().__init__(HTTPStatus.BAD_REQUEST, message, details=details)


class NotFoundError(ApiError):
    def __init__(self, message: str = "Not found.", *, details: dict | None = None):
        super().__init__(HTTPStatus.NOT_FOUND, message, details=details)


class ConflictError(ApiError):
    def __init__(self, message: str, *, details: dict | None = None):
        super().__init__(HTTPStatus.CONFLICT, message, details=details)


@dataclass(frozen=True)
class Response:
    """Explicit HTTP response when a handler needs custom status, bytes, or content type."""

    data: bytes | dict | list
    status: HTTPStatus = HTTPStatus.OK
    content_type: str = "application/json; charset=utf-8"
    headers: dict[str, str] = field(default_factory=dict)

    def encode(self):
        if isinstance(self.data, bytes):
            return self.data, self.content_type
        return json.dumps(self.data).encode("utf-8"), self.content_type


@dataclass
class Request:
    """Parsed HTTP request passed to a route handler."""

    method: str
    path: str
    params: dict[str, str]
    query: dict[str, list[str]]
    raw_body: bytes | None = None
    _json_loaded: bool = field(default=False, init=False, repr=False)
    _json_value: object = field(default=None, init=False, repr=False)

    def arg(self, name: str, default=None):
        values = self.query.get(name)
        return values[0] if values else default

    def json(self):
        if not self._json_loaded:
            if not self.raw_body:
                raise BadRequestError("Request body must be valid JSON.")
            try:
                self._json_value = json.loads(self.raw_body.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError) as error:
                raise BadRequestError(f"Malformed JSON body: {error}") from error
            self._json_loaded = True
        return self._json_value


def _compile_pattern(pattern: str):
    parts = ["^"]
    pos = 0
    for match in PARAM_PATTERN.finditer(pattern):
        parts.append(re.escape(pattern[pos:match.start()]))
        name, kind = match.group(1), match.group(2)
        segment = ".+" if kind == "path" else "[^/]+"
        parts.append(f"(?P<{name}>{segment})")
        pos = match.end()
    parts.append(re.escape(pattern[pos:]))
    parts.append("$")
    return re.compile("".join(parts))


@dataclass(frozen=True)
class Route:
    method: str
    pattern: str
    regex: re.Pattern
    handler: object


class Router:
    """Explicit route table supporting GET, POST, PUT, DELETE and named path params."""

    def __init__(self):
        self._routes: list[Route] = []

    def add(self, method: str, pattern: str, handler):
        verb = method.upper()
        if verb not in METHODS:
            raise ValueError(f"Unsupported HTTP method '{method}'.")
        self._routes.append(Route(verb, pattern, _compile_pattern(pattern), handler))
        return handler

    def get(self, pattern: str, handler):
        return self.add("GET", pattern, handler)

    def post(self, pattern: str, handler):
        return self.add("POST", pattern, handler)

    def put(self, pattern: str, handler):
        return self.add("PUT", pattern, handler)

    def delete(self, pattern: str, handler):
        return self.add("DELETE", pattern, handler)

    def match(self, method: str, raw_path: str):
        """Return (handler, decoded_params, allowed_methods).

        If a route matches method + raw_path, handler is returned.
        If routes match raw_path only for other methods, handler is None and allowed_methods is non-empty.
        """
        verb = method.upper()
        allowed = []
        for route in self._routes:
            matched = route.regex.match(raw_path)
            if not matched:
                continue
            if route.method == verb:
                params = {name: unquote(value) for name, value in matched.groupdict().items()}
                return route.handler, params, ()
            if route.method not in allowed:
                allowed.append(route.method)
        return None, {}, tuple(allowed)

    def dispatch(self, method: str, target: str, raw_body: bytes | None = None) -> Response | None:
        """Dispatch an HTTP request through the route table.

        Returns a Response if a route matched (or for 405 / /api/* 404), or None for non-API GET
        paths that should fall through to static/media file serving.
        """
        url = urlsplit(target)
        handler, params, allowed = self.match(method, url.path)
        if handler is None:
            if allowed:
                return Response(
                    {"error": f"Method {method.upper()} not allowed."},
                    HTTPStatus.METHOD_NOT_ALLOWED,
                    headers={"Allow": ", ".join(allowed)},
                )
            if method.upper() == "GET" and not url.path.startswith("/api/"):
                return None
            return Response({"error": "Not found."}, HTTPStatus.NOT_FOUND)

        request = Request(
            method=method.upper(),
            path=unquote(url.path),
            params=params,
            query=parse_qs(url.query),
            raw_body=raw_body,
        )
        try:
            result = handler(request)
        except ApiError as error:
            return Response(error.payload(), error.status)
        except (KeyError, ValueError) as error:
            return Response({"error": str(error)}, HTTPStatus.BAD_REQUEST)

        if isinstance(result, Response):
            return result
        return Response(result, HTTPStatus.OK)
