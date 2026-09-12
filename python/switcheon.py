"""SwitcheOn API client for Python 3.9 or later.

Covers what an integration needs: log in with the email and password from the
phone app, read the account and its boxes, add a box from its QR code, switch
channels, and receive live updates as boxes check in.

REST calls use requests. Live updates speak the SignalR JSON protocol directly
over a websocket, since there is no official SignalR client for Python; the
protocol is small and is described in ../README.md.
"""

import asyncio
import base64
import itertools
import json
import os
import re
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Awaitable, Callable, Dict, List, Optional, Union

import requests
import websockets

DEFAULT_BASE_URL = "https://www.switcheon.com"

# Tokens last eight hours. Renew a little early so a request never goes out
# with a token that expires in flight.
TOKEN_REFRESH_MARGIN_SECONDS = 300

# The SignalR JSON protocol ends every message with this character.
RECORD_SEPARATOR = "\x1e"

# The server drops a connection it hasn't heard from in 30 seconds, so the
# client has to speak up more often than that even when it has nothing to say.
PING_INTERVAL_SECONDS = 15


class SwitcheOnError(Exception):
    def __init__(self, message: str, status: Optional[int] = None):
        super().__init__(message)
        self.status = status


def new_user_id_bin() -> str:
    """A fresh random user id, for registering an account directly through the
    API rather than through the phone app. Every id is 16 bytes sent as
    standard base64."""
    return base64.b64encode(os.urandom(16)).decode("ascii")


def box_id_bin_from_text(text: str) -> str:
    """Turns anything that identifies a box into the base64 id the API expects:
    the text of the QR code on the box, or the 32 hex digit BoxIdText, with or
    without dashes."""
    hex_id = text.strip().rsplit("/", 1)[-1].replace("-", "")
    if not re.fullmatch(r"[0-9a-fA-F]{32}", hex_id):
        raise ValueError(f"Not a SwitcheOn QR code or box id: {text}")
    return base64.b64encode(bytes.fromhex(hex_id)).decode("ascii")


def channel_is_on(status: int, channel: int) -> bool:
    """Whether a 1-based channel is on in a status bitmask."""
    return (status >> (channel - 1)) & 1 == 1


def with_channel(status: int, channel: int, on: bool) -> int:
    """A status bitmask with one 1-based channel switched on or off."""
    bit = 1 << (channel - 1)
    return status | bit if on else status & ~bit


@dataclass
class LiveUpdate:
    """One live update, normalised.

    The server sends each update as a JSON string, and only the short lowercase
    fields carry information. The rest are defaults that would look like real
    values (CurrentStatus 0, Channels 0) if merged into a box, so they are left
    in raw rather than copied out.
    """

    box_id_bin: str
    kind: str  # "checkin" from the box, or "request" when someone asks it to switch
    received_at: datetime  # box updates carry no timestamp of their own
    status: Optional[int]
    requested_status: Optional[int]
    exclusive: Optional[int]
    temperature: Optional[float]
    sequence: Optional[int]
    analog: Optional[List[int]]
    log: Optional[List[Dict[str, Any]]]
    raw: Dict[str, Any] = field(repr=False)

    @classmethod
    def from_message(cls, message: Union[str, Dict[str, Any]]) -> "LiveUpdate":
        raw = json.loads(message) if isinstance(message, str) else message
        return cls(
            box_id_bin=raw["BoxIdBin"],
            # A request echo always carries req; a check-in from the box never does
            kind="request" if raw.get("req") is not None else "checkin",
            received_at=datetime.now(timezone.utc),
            status=raw.get("stat"),
            requested_status=raw.get("req"),
            exclusive=raw.get("excl"),
            temperature=raw.get("temp"),
            sequence=raw.get("seq"),
            analog=raw.get("anlg"),
            log=raw.get("log"),
            raw=raw,
        )


class SwitcheOnClient:
    def __init__(self, base_url: str = DEFAULT_BASE_URL, session: Optional[requests.Session] = None):
        self.base_url = base_url.rstrip("/")
        self.session = session or requests.Session()
        #: The logged in account's id, set by login().
        self.user_id_bin: Optional[str] = None
        self._email: Optional[str] = None
        self._password: Optional[str] = None
        self._token: Optional[str] = None
        self._token_expires = 0.0

    # -- REST ---------------------------------------------------------------

    def login(self, email: str, password: str) -> None:
        """Logs in with the email and password set in the phone app. The
        credentials are kept in memory so the token can be renewed before it
        expires."""
        # Form encoded, not JSON: a JSON body is ignored and comes back as a 401
        res = self.session.post(
            f"{self.base_url}/api/User",
            data={"Email": email, "UserPassword": password},
            timeout=30,
        )
        if res.status_code == 401:
            raise SwitcheOnError("The email or password was not accepted", 401)
        if not res.ok:
            raise SwitcheOnError(f"Login failed with HTTP {res.status_code}", res.status_code)

        body = res.json()
        self._email, self._password = email, password
        self.user_id_bin = body["userIdBin"]
        self._token = body["token"]
        self._token_expires = _token_expiry(body["token"])

    def get_user(self) -> Dict[str, Any]:
        """The account and every box on it."""
        res = self.session.get(
            f"{self.base_url}/api/User",
            params={"UserIdBin": self._require_user(), "UserSecret": self._secret()},
            timeout=30,
        )
        # A rejected token comes back as 404, not 401
        if res.status_code == 404:
            raise SwitcheOnError("The account was not found or the token was rejected", 404)
        if not res.ok:
            raise SwitcheOnError(f"GET /api/User failed with HTTP {res.status_code}", res.status_code)
        return res.json()

    def add_box(self, qr_or_box_id: str) -> str:
        """Adds a box to the account from its QR code or box id. The first
        account on a box becomes its owner, and adding a box that has never been
        activated starts its cellular activation. Returns the server's message,
        which may be empty."""
        return self._put("/api/BoxUser", {"boxIdBin": box_id_bin_from_text(qr_or_box_id)})

    def set_status(self, box_id_bin: str, status: int) -> None:
        """Asks a box to set every channel at once. Bit 0 is channel 1. This is
        the whole state, not a toggle, and bits beyond the box's channel count
        are dropped.

        Returns once the request is queued. The box applies it on its next
        exchange with the server, and the change shows up as a live update and
        in currentStatus."""
        if not isinstance(status, int) or not 0 <= status <= 255:
            raise ValueError(f"Status must be a bitmask from 0 to 255, got {status!r}")
        self._put("/api/req", {"boxIdBin": box_id_bin, "requestedStatus": status})

    def set_channel(self, box_id_bin: str, channel: int, on: bool) -> None:
        """Switches one 1-based channel and leaves the others as they were. "As
        they were" means the most recent request if one is still pending,
        otherwise what the box last reported, so two quick calls don't undo
        each other."""
        box = next((b for b in self.get_user()["boxes"] if b["boxIdBin"] == box_id_bin), None)
        if box is None:
            raise SwitcheOnError(f"Box {box_id_bin} is not on this account")
        if not 1 <= channel <= box["channels"]:
            raise ValueError(f"Box has channels 1 to {box['channels']}, got {channel}")
        current = box["pendingStatus"] if box["pendingStatus"] is not None else box["currentStatus"]
        self.set_status(box_id_bin, with_channel(current, channel, on))

    # -- Live updates -------------------------------------------------------

    async def live_updates(
        self,
        on_update: Callable[[LiveUpdate], Union[None, Awaitable[None]]],
        on_state_change: Callable[[str], None] = lambda state: None,
    ) -> None:
        """Receives live updates until cancelled, reconnecting on its own.

        on_update is called with a LiveUpdate each time a box on the account
        checks in or someone sends it a request. It may be a plain function or
        a coroutine function."""
        retry = 0
        while True:
            try:
                await self._run_connection(on_update, on_state_change)
                retry = 0
            except asyncio.CancelledError:
                raise
            except Exception as error:  # a dropped connection is routine; say so and go again
                on_state_change(f"disconnected: {error}")
            delay = min(30, 2**retry)
            retry += 1
            on_state_change("reconnecting")
            await asyncio.sleep(delay)

    async def _run_connection(self, on_update, on_state_change) -> None:
        url = re.sub(r"^http", "ws", self.base_url) + "/api/userhub"
        invocation_ids = itertools.count(1)

        # Connecting straight to the websocket skips SignalR's negotiate step,
        # which the server allows for websocket clients.
        async with websockets.connect(url, ping_interval=None, open_timeout=30) as ws:
            await _send(ws, {"protocol": "json", "version": 1})
            handshake = _parse_frame(await asyncio.wait_for(ws.recv(), 30))
            if handshake and handshake[0].get("error"):
                raise SwitcheOnError(f"SignalR handshake refused: {handshake[0]['error']}")

            # The secret is fetched off the event loop because renewing it is a blocking HTTP call
            secret = await asyncio.to_thread(self._secret)
            register_id = str(next(invocation_ids))
            await _send(ws, {
                "type": 1,
                "invocationId": register_id,
                "target": "registerConnectionSecure",
                "arguments": [self._require_user(), secret],
            })

            pinger = asyncio.create_task(_keep_alive(ws))
            try:
                async for frame in ws:
                    for message in _parse_frame(frame):
                        kind = message.get("type")
                        if kind == 1 and message.get("target") == "updateFromServer":
                            result = on_update(LiveUpdate.from_message(message["arguments"][0]))
                            if asyncio.iscoroutine(result):
                                await result
                        elif kind == 3 and message.get("invocationId") == register_id:
                            if message.get("error"):
                                raise SwitcheOnError(f"Registration refused: {message['error']}")
                            on_state_change("connected")
                        elif kind == 7:
                            raise SwitcheOnError(f"Server closed the connection: {message.get('error') or 'no reason given'}")
                        # type 6 is the server's ping, which needs no reply
            finally:
                pinger.cancel()

    # -- Plumbing -----------------------------------------------------------

    def _require_user(self) -> str:
        if not self.user_id_bin:
            raise SwitcheOnError("Call login() first")
        return self.user_id_bin

    def _secret(self) -> str:
        if not self._token:
            raise SwitcheOnError("Call login() first")
        if time.time() > self._token_expires - TOKEN_REFRESH_MARGIN_SECONDS:
            self.login(self._email, self._password)
        return self._token

    def _put(self, path: str, fields: Dict[str, Any]) -> str:
        body = {"userIdBin": self._require_user(), "userSecret": self._secret(), **fields}
        res = self.session.put(f"{self.base_url}{path}", json=body, timeout=30)
        if not res.ok:
            raise SwitcheOnError(f"PUT {path} failed with HTTP {res.status_code}", res.status_code)
        return res.text


def _token_expiry(token: str) -> float:
    # The token is "jwt:" followed by a standard JWT. Only the expiry is read
    # here; the server is what checks the signature.
    payload = token[len("jwt:"):].split(".")[1]
    payload += "=" * (-len(payload) % 4)
    return float(json.loads(base64.urlsafe_b64decode(payload))["exp"])


async def _send(ws, message: Dict[str, Any]) -> None:
    await ws.send(json.dumps(message) + RECORD_SEPARATOR)


def _parse_frame(frame: Union[str, bytes]) -> List[Dict[str, Any]]:
    # One websocket frame can hold several SignalR messages
    text = frame.decode("utf-8") if isinstance(frame, bytes) else frame
    return [json.loads(part) for part in text.split(RECORD_SEPARATOR) if part]


async def _keep_alive(ws) -> None:
    while True:
        await asyncio.sleep(PING_INTERVAL_SECONDS)
        await _send(ws, {"type": 6})
