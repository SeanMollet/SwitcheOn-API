"""Command line walkthrough of the client. Run with no arguments for usage.

    export SWITCHEON_EMAIL=you@example.com SWITCHEON_PASSWORD=...
    python example.py boxes
    python example.py set 4129B3480C8D4C76888DEFAFD416458E 2 on
    python example.py watch
"""

import asyncio
import os
import sys

from switcheon import (
    DEFAULT_BASE_URL,
    LiveUpdate,
    SwitcheOnClient,
    box_id_bin_from_text,
    channel_is_on,
)

USAGE = f"""Usage: python example.py <command>

  boxes                          list the boxes on the account
  add <qr code text>             add a box from the text of its QR code
  set <box id> <channel> on|off  switch one channel, numbered from 1
  watch                          print live updates until Ctrl+C

Environment: SWITCHEON_EMAIL, SWITCHEON_PASSWORD, and optionally SWITCHEON_URL
(default {DEFAULT_BASE_URL})."""


def require_env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        print(f"{name} is not set.\n\n{USAGE}", file=sys.stderr)
        sys.exit(2)
    return value


def describe_channels(status: int, count: int, names) -> str:
    parts = []
    for channel in range(1, count + 1):
        name = names[channel - 1] if names and len(names) >= channel and names[channel - 1] else f"Channel {channel}"
        parts.append(f"{name} {'on' if channel_is_on(status, channel) else 'off'}")
    return ", ".join(parts)


def print_update(update: LiveUpdate) -> None:
    time = update.received_at.astimezone().strftime("%H:%M:%S")
    if update.kind == "request":
        print(f"{time}  {update.box_id_bin}  requested status {update.requested_status}", flush=True)
    else:
        print(f"{time}  {update.box_id_bin}  status {update.status}  temp {update.temperature}", flush=True)


def main(argv) -> int:
    if not argv or argv[0] not in ("boxes", "add", "set", "watch"):
        print(USAGE)
        return 2 if argv else 0
    command, args = argv[0], argv[1:]

    client = SwitcheOnClient(os.environ.get("SWITCHEON_URL", DEFAULT_BASE_URL))
    client.login(require_env("SWITCHEON_EMAIL"), require_env("SWITCHEON_PASSWORD"))

    if command == "boxes":
        user = client.get_user()
        print(f"{user['firstname']} {user['lastname']} <{user['email']}>, {len(user['boxes'])} box(es)")
        for box in user["boxes"]:
            print(f"\n{box['boxIdText']}  {box['location'] or '(no location)'}  {'online' if box['online'] else 'offline'}")
            print("  " + describe_channels(box["currentStatus"], box["channels"], box["channelNames"]))
            if box["pendingStatus"] is not None and box["pendingStatus"] != box["currentStatus"]:
                print("  requested: " + describe_channels(box["pendingStatus"], box["channels"], box["channelNames"]))

    elif command == "add":
        if len(args) != 1:
            print(USAGE, file=sys.stderr)
            return 2
        print(client.add_box(args[0]) or "Box added.")

    elif command == "set":
        if len(args) != 3 or args[2] not in ("on", "off") or not args[1].isdigit():
            print(USAGE, file=sys.stderr)
            return 2
        client.set_channel(box_id_bin_from_text(args[0]), int(args[1]), args[2] == "on")
        print(f"Requested channel {args[1]} {args[2]}. The box applies it on its next check-in.")

    elif command == "watch":
        print("Watching for updates. Ctrl+C to stop.", flush=True)
        try:
            asyncio.run(client.live_updates(print_update, lambda state: print(f"[{state}]", flush=True)))
        except KeyboardInterrupt:
            pass

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception as error:
        print(f"Error: {error}", file=sys.stderr)
        sys.exit(1)
