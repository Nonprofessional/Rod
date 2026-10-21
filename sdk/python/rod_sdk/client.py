"""The official Rod Python SDK (architecture.md Sec 15.3, ADR-006).

A thin, dependency-free client over the versioned operator API (``/api/v1``,
OpenAPI 3.1 -- the committed ``sdk/openapi.v1.json`` is the interface of
record). Everything rides the standard library: cookie sessions through
``http.cookiejar``, bearer API tokens through the ``Authorization`` header,
and the live event stream through plain SSE line parsing.

Two authentication shapes, one client:

- ``login(handle, password)`` establishes the same cookie session the
  operator console uses -- full operator authority, scoped by nothing but
  the deployment role and the engagement memberships.
- ``RodClient(url, token=secret)`` authenticates every request with a
  minted API token whose scope (read / operate / admin) caps what the
  credential may do server-side.

Responses are plain dicts and lists mirroring the OpenAPI schemas; the
task and artifact listings are cursor-paged (``limit`` + opaque
``nextCursor``).
"""

from __future__ import annotations

import http.cookiejar
import json
import urllib.error
import urllib.request
from typing import Any, Iterator

__all__ = ["RodClient", "RodError"]


class RodError(Exception):
    """A non-success answer from the teamserver.

    Carries the HTTP status and the API's single error envelope
    (``{"error": ...}``) when the body is shaped that way.
    """

    def __init__(self, status: int, message: str) -> None:
        super().__init__(f"{status}: {message}")
        self.status = status
        self.message = message


class RodClient:
    """One teamserver connection. Thread-unsafe by design: the cookie jar
    and the underlying opener are shared state."""

    def __init__(self, base_url: str, token: str | None = None) -> None:
        self._base = base_url.rstrip("/")
        self._token = token
        self._cookies = http.cookiejar.CookieJar()
        self._opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self._cookies))

    # -- session ----------------------------------------------------------

    def login(self, handle: str, password: str) -> dict[str, Any]:
        """Establish the cookie session; returns the operator summary."""
        return self._request("POST", "/operators/login",
                             body={"handle": handle, "password": password})

    def logout(self) -> None:
        self._request("POST", "/operators/logout")

    def me(self) -> dict[str, Any]:
        return self._request("GET", "/operators/me")

    # -- API tokens -------------------------------------------------------

    def mint_token(self, operator_id: str, scope: str = "read") -> dict[str, Any]:
        """Mint a bearer token; the secret is shown exactly once, here."""
        if scope not in ("read", "operate", "admin"):
            raise ValueError("scope must be one of: read, operate, admin")
        return self._request("POST", f"/operators/{operator_id}/tokens",
                             body={"scope": scope})

    def list_tokens(self, operator_id: str) -> list[dict[str, Any]]:
        return self._request("GET", f"/operators/{operator_id}/tokens")

    def revoke_token(self, operator_id: str, token_id: str) -> None:
        self._request("POST", f"/operators/{operator_id}/tokens/{token_id}:revoke")

    # -- engagements ------------------------------------------------------

    def create_engagement(self, name: str, description: str | None = None) -> dict[str, Any]:
        return self._request("POST", "/engagements",
                             body={"name": name, "description": description})

    def list_engagements(self) -> list[dict[str, Any]]:
        return self._request("GET", "/engagements")

    # -- implants ---------------------------------------------------------

    def list_implants(self, engagement_id: str) -> list[dict[str, Any]]:
        return self._request("GET", f"/engagements/{engagement_id}/implants")

    # -- tasking ----------------------------------------------------------

    def issue_task(
        self,
        engagement_id: str,
        implant_id: str,
        verb: str,
        arguments: str | None = None,
        content: bytes | None = None,
    ) -> dict[str, Any]:
        """Issue a task; ``content`` carries a bulk payload as staged bytes
        (the arguments string gains the payload's sha256)."""
        if content is not None:
            import base64
            encoded = base64.b64encode(content).decode("ascii")
        else:
            encoded = None
        return self._request(
            "POST",
            f"/engagements/{engagement_id}/tasks",
            body={
                "implantId": implant_id,
                "verb": verb,
                "arguments": arguments,
                "content": encoded,
            })

    def get_task(self, engagement_id: str, task_id: str) -> dict[str, Any]:
        return self._request("GET", f"/engagements/{engagement_id}/tasks/{task_id}")

    def list_tasks(
        self, engagement_id: str, limit: int | None = None, cursor: str | None = None,
    ) -> dict[str, Any]:
        return self._request("GET", f"/engagements/{engagement_id}/tasks",
                             query=self._paging(limit, cursor))

    def cancel_task(self, engagement_id: str, task_id: str) -> None:
        self._request("POST", f"/engagements/{engagement_id}/tasks/{task_id}:cancel")

    def send_task_input(self, engagement_id: str, task_id: str, data: bytes) -> None:
        self._request("POST", f"/engagements/{engagement_id}/tasks/{task_id}/input",
                      body=data, content_type="application/octet-stream")

    # -- artifacts --------------------------------------------------------

    def list_artifacts(
        self, engagement_id: str, task_id: str, limit: int | None = None,
        cursor: str | None = None,
    ) -> dict[str, Any]:
        return self._request(
            "GET",
            f"/engagements/{engagement_id}/tasks/{task_id}/artifacts",
            query=self._paging(limit, cursor))

    def get_artifact(self, engagement_id: str, artifact_id: str) -> bytes:
        status, _, body = self._raw(
            "GET", f"/engagements/{engagement_id}/artifacts/{artifact_id}")
        if status != 200:
            raise RodError(status, body.decode("utf-8", "replace"))
        return body

    # -- live events ------------------------------------------------------

    def events(self, engagement_id: str, timeout: float | None = 60.0) -> Iterator[dict[str, Any]]:
        """The engagement's live SSE stream as parsed event dicts
        (``{"event": ..., "data": {...}``). A generator: consume it in a
        ``for`` loop; closing the consumer's interest means breaking out."""
        import socket
        request = urllib.request.Request(
            self._url(f"/engagements/{engagement_id}/events"))
        self._authorize(request)
        response = self._opener.open(request, timeout=timeout)
        try:
            event_name: str | None = None
            data_lines: list[str] = []
            for raw in response:
                line = raw.decode("utf-8", "replace").rstrip("\r\n")
                if line == "":
                    if event_name is not None or data_lines:
                        yield {
                            "event": event_name or "message",
                            "data": "\n".join(data_lines),
                        }
                    event_name, data_lines = None, []
                elif line.startswith(":"):
                    continue  # SSE comment / keep-alive
                elif line.startswith("event:"):
                    event_name = line[len("event:"):].strip()
                elif line.startswith("data:"):
                    data_lines.append(line[len("data:"):].strip())
        except (socket.timeout, TimeoutError):
            return

    # -- plumbing ---------------------------------------------------------

    @staticmethod
    def _paging(limit: int | None, cursor: str | None) -> list[tuple[str, str]]:
        query: list[tuple[str, str]] = []
        if limit is not None:
            query.append(("limit", str(limit)))
        if cursor is not None:
            query.append(("cursor", cursor))
        return query

    def _url(self, path: str, query: list[tuple[str, str]] | None = None) -> str:
        from urllib.parse import urlencode
        url = f"{self._base}/api/v1{path}"
        if query:
            url += "?" + urlencode(query)
        return url

    def _authorize(self, request: urllib.request.Request) -> None:
        if self._token is not None:
            request.add_header("Authorization", f"Bearer {self._token}")

    def _request(
        self,
        method: str,
        path: str,
        body: Any = None,
        query: list[tuple[str, str]] | None = None,
        content_type: str = "application/json",
    ) -> Any:
        payload: bytes | None = None
        if body is not None:
            if isinstance(body, (bytes, bytearray)):
                payload = bytes(body)
            else:
                payload = json.dumps(body).encode("utf-8")
        status, headers, response_body = self._raw(
            method, path, payload, query,
            content_type if payload is not None else None)
        if status >= 400:
            raise RodError(status, _error_message(status, response_body))
        if not response_body:
            return None
        return json.loads(response_body.decode("utf-8"))

    def _raw(
        self,
        method: str,
        path: str,
        payload: bytes | None = None,
        query: list[tuple[str, str]] | None = None,
        content_type: str | None = None,
    ) -> tuple[int, Any, bytes]:
        request = urllib.request.Request(self._url(path, query), data=payload,
                                         method=method)
        if content_type is not None and payload is not None:
            request.add_header("Content-Type", content_type)
        self._authorize(request)
        try:
            response = self._opener.open(request, timeout=60)
        except urllib.error.HTTPError as error:
            return error.code, error.headers, error.read()
        with response:
            return response.status, response.headers, response.read()


def _error_message(status: int, body: bytes) -> str:
    try:
        parsed = json.loads(body.decode("utf-8"))
        if isinstance(parsed, dict) and "error" in parsed:
            return str(parsed["error"])
    except (ValueError, UnicodeDecodeError):
        pass
    return f"HTTP {status} with no error body"
