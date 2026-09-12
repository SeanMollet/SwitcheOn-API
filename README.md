# SwitcheOn API

Read and control SwitcheOn boxes from your own code, and get told the moment a box
reports in.

This guide covers the four calls an integration needs and the live update connection.
Working clients in three languages sit beside it, and each one runs the same small
command line example:

| Folder | Runtime | Live updates via |
|---|---|---|
| [`javascript/`](javascript) | Node.js 18+ | `@microsoft/signalr` |
| [`python/`](python) | Python 3.9+ | the SignalR JSON protocol over `websockets` |
| [`csharp/`](csharp) | .NET 10 | `Microsoft.AspNetCore.SignalR.Client` |

The clients are written to be read. Each one is a single file, and the comments explain
the behaviour of the API that shaped the code.

## Quick start

Set an email and password for your account in the SwitcheOn phone app, then:

```sh
export SWITCHEON_EMAIL=you@example.com
export SWITCHEON_PASSWORD='your app password'

# JavaScript
cd javascript && npm install
node example.mjs boxes

# Python
cd python && python -m venv .venv && . .venv/bin/activate && pip install -r requirements.txt
python example.py boxes

# C#
cd csharp
dotnet run -- boxes
```

Every example takes the same commands:

```text
boxes                          list the boxes on the account
add <qr code text>             add a box from the text of its QR code
set <box id> <channel> on|off  switch one channel, numbered from 1
watch                          print live updates until Ctrl+C
```

## Concepts

**Ids are 16 bytes, sent as standard base64.** An account is identified by its
`userIdBin` and a box by its `boxIdBin`, for example `QSmzSAyNTHaIje+v1BZFjg==`. Use
standard base64 with `+`, `/` and `=`, not the URL-safe variant. When an id goes in a
query string it must be URL encoded, because `+` would otherwise arrive as a space.

**Boxes also have a hex form.** `boxIdText` is the same 16 bytes as 32 hex digits, for
example `4129B3480C8D4C76888DEFAFD416458E`. It is what the QR code on the box carries:

```text
HTTPS://SWITCHEON.COM/ACT/4129B3480C8D4C76888DEFAFD416458E
```

To get the `boxIdBin` from a scan, take the last path segment, decode the hex, and base64
encode the bytes. Every client has a helper for this.

**Channel state is a bitmask.** Bit 0 is channel 1, bit 1 is channel 2, and so on. A box
with channels 1 and 3 on has a status of `5`.

## Authentication

Every call identifies the account with `userIdBin` and proves it with `userSecret`. There
are two ways to get a secret.

### Recommended: log in with the phone app account

Set an email and password in the phone app, then log in with them. The response carries
the account's id and a token:

```sh
curl -X POST https://www.switcheon.com/api/User \
  --data-urlencode 'Email=you@example.com' \
  --data-urlencode 'UserPassword=your app password'
```

```json
{
  "userIdBin": "vZYCe0jbSyq5xs3SER6Bfg==",
  "token": "jwt:eyJ0eXAiOiJKV1QiLCJhbGciOiJFUzI1NiJ9..."
}
```

Send the whole token, `jwt:` prefix included, as `userSecret` on every other call.

- **Tokens last eight hours.** Log in again before then. The clients read the `exp` claim
  and renew five minutes early, so a long-running process never notices.
- **The body must be form encoded.** A JSON body is ignored and answered with `401`.
- **A bad email or password returns `401`.**

This route ties the integration to the account you already use, so the boxes you see in
the app are the boxes you see in the API.

### Alternative: an account created through the API

1. Generate 16 random bytes and base64 encode them. That is your `userIdBin`.
2. Choose a long random secret.
3. Register both:

```sh
curl -X PUT https://www.switcheon.com/api/User \
  -H 'Content-Type: application/json' \
  -d '{
        "userIdBin": "vZYCe0jbSyq5xs3SER6Bfg==",
        "userSecret": "a long random secret",
        "firstname": "Integration",
        "lastname": "Account",
        "email": "integration@example.com",
        "password": "optional, lets you log in as above"
      }'
```

Then send the secret itself as `userSecret`. It does not expire. The new account starts
with no boxes, so add them from their QR codes.

### Keeping it private

The token, the secret and the password are credentials. The `userIdBin` is not: it is
readable inside every token, so never rely on it being unknown.

## Endpoints

All endpoints are under `https://www.switcheon.com`.

### Log in

`POST /api/User`, form encoded. Covered in [Authentication](#authentication).

### Get the account and its boxes

`GET /api/User?UserIdBin=…&UserSecret=…`

```sh
curl -G https://www.switcheon.com/api/User \
  --data-urlencode 'UserIdBin=vZYCe0jbSyq5xs3SER6Bfg==' \
  --data-urlencode 'UserSecret=jwt:eyJ0eXAi...'
```

```json
{
  "firstname": "Api",
  "lastname": "Docs",
  "email": "you@example.com",
  "billingStatus": null,
  "boxes": [
    {
      "boxIdBin": "QSmzSAyNTHaIje+v1BZFjg==",
      "boxIdText": "4129B3480C8D4C76888DEFAFD416458E",
      "imei": 990000004711232,
      "iccid": 89001010000000471128,
      "location": "Barn",
      "deviceType": "P",
      "channels": 4,
      "channelNames": ["Pump", "Lights", "", ""],
      "currentStatus": 5,
      "pendingStatus": 7,
      "online": 1,
      "temp": 71.6,
      "celcius": false,
      "signalStrength": 22,
      "paidUntil": "2027-09-30T23:59:00Z",
      "owner": { "userIdBin": "vZYCe0jbSyq5xs3SER6Bfg==", "owner": true, "firstname": "Api", "lastname": "Docs" },
      "users": [ { "userIdBin": "vZYCe0jbSyq5xs3SER6Bfg==", "owner": true, "firstname": "Api", "lastname": "Docs" } ]
    }
  ]
}
```

The response has more fields than shown. These are the ones that matter for control:

| Field | Meaning |
|---|---|
| `channels` | How many channels the box has |
| `channelNames` | Names set in the app, one per channel, possibly empty or shorter than `channels` |
| `currentStatus` | Channel bitmask the box last reported |
| `pendingStatus` | Bitmask of the newest request the box hasn't applied yet, or `null` when nothing is waiting |
| `online` | `1` if the box is currently checking in |
| `temp` | Temperature in the unit the box is set to; see `celcius` |
| `channelsExclusive` | Bitmask of channels that may not be on at the same time |

**A rejected token returns `404`, not `401`.** Treat `404` from this call as "log in again".

### Add a box

`PUT /api/BoxUser`, JSON.

```sh
curl -X PUT https://www.switcheon.com/api/BoxUser \
  -H 'Content-Type: application/json' \
  -d '{"userIdBin":"vZYCe0jbSyq5xs3SER6Bfg==","userSecret":"jwt:eyJ0eXAi...","boxIdBin":"QSmzSAyNTHaIje+v1BZFjg=="}'
```

This is the same thing the app does when you scan a box, with the same consequences:

- **The first account on a box becomes its owner.** Later accounts are added as users.
- **A box that has never been activated starts cellular activation.** The response text
  says so, and activation takes between five minutes and eight hours.
- **A box its owner has marked exclusive can't be added by anyone else.**

| Response | Meaning |
|---|---|
| `200` | Added. The body is a message for the user, which may be empty |
| `401` | The credentials were rejected |
| `404` | No box has that id |
| `400` | Anything else, including a box that is already on this account |

### Switch channels

`PUT /api/req`, JSON.

```sh
curl -X PUT https://www.switcheon.com/api/req \
  -H 'Content-Type: application/json' \
  -d '{"userIdBin":"vZYCe0jbSyq5xs3SER6Bfg==","userSecret":"jwt:eyJ0eXAi...","boxIdBin":"QSmzSAyNTHaIje+v1BZFjg==","requestedStatus":5}'
```

`requestedStatus` is the state of **every** channel, not a change to one of them. Bits past
the box's channel count are dropped. To switch a single channel, start from
`pendingStatus` if it isn't `null`, otherwise `currentStatus`, set or clear the one bit,
and send the result. Starting from the pending request means two quick changes don't
undo each other. The clients' `setChannel` does exactly this.

**Always send `requestedStatus`.** A request without it is treated as `0`, which turns
every channel off.

| Response | Meaning |
|---|---|
| `200` with body `Success` | The request is queued. It has not been applied yet |
| `500` | The credentials were rejected, or the box isn't on this account |
| `400` | The request couldn't be processed |

A `200` means the server accepted the request, not that the box has switched. The box
picks it up on its next exchange with the server. When it does, `currentStatus` changes
and a live update arrives with the new `stat`.

## Live updates

The server pushes an update to the account every time one of its boxes checks in, and
every time anyone sends one of its boxes a request. It uses
[SignalR](https://learn.microsoft.com/aspnet/core/signalr/introduction) with the JSON
protocol, at:

```text
wss://www.switcheon.com/api/userhub
```

### Connecting

1. Connect to the hub.
2. Invoke `registerConnectionSecure` with two arguments: the `userIdBin` and the same
   `userSecret` you send to the REST calls.
3. Listen for `updateFromServer`.

**Register again after every reconnect.** The server forgets a registration when the
connection drops. The clients hook the reconnect event and register again.

### Only one live connection per account

The server keeps a single live connection for each account, and the newest registration
replaces the previous one. If your integration and the phone app are logged in as the
same account, whichever registered last receives the updates and the other goes quiet.

If you need live updates in the app and in your integration at the same time, create a
separate account for the integration and add the boxes to it. Updates go to every
account on a box, and each account keeps its own connection.

### The update message

`updateFromServer` has one argument: **a JSON string**, which has to be parsed a second
time. An update is one of two kinds.

A **check-in** from the box:

```json
{"temp":71.6,"stat":5,"excl":0,"req":null,"seq":118,"anlg":null,"log":null,
 "BoxIdBin":"QSmzSAyNTHaIje+v1BZFjg==","CurrentStatus":5,"Temp":71.6,"IMEI":0,"Channels":0, …}
```

A **request echo**, sent the moment anyone asks the box to switch:

```json
{"temp":null,"stat":null,"excl":null,"req":7,"seq":null,"anlg":null,"log":null,
 "BoxIdBin":"QSmzSAyNTHaIje+v1BZFjg==","CurrentStatus":0,"Temp":null,"IMEI":0,"Channels":0, …}
```

Read only these fields:

| Field | Present on | Meaning |
|---|---|---|
| `BoxIdBin` | both | Which box |
| `stat` | check-in | Channel bitmask the box has now |
| `temp` | check-in | Temperature, in the unit the box is set to |
| `excl` | check-in | Exclusive channel bitmask |
| `seq` | check-in | The box's update counter |
| `anlg` | check-in | Analog readings, on boxes that have analog inputs |
| `log` | check-in | Error log entries the box sent, if any |
| `req` | request echo | The channel bitmask that was just requested |

The message carries every other box field too, but they aren't filled in. `IMEI` and
`Channels` arrive as `0`, and a request echo carries `CurrentStatus: 0` whatever the box
is actually doing. **Don't merge the capitalised fields into your copy of a box.** Tell
the kinds apart by `req`, which a request echo always has and a check-in never does.

Updates carry no timestamp, so record when each one arrives.

Changes to `online` aren't pushed. A check-in means the box is online; to notice a box
going offline, read `online` from `GET /api/User` now and then.

### Speaking the protocol directly

Where there is no SignalR client for your language, the protocol is small. The Python
client implements it in about sixty lines.

Every message is JSON followed by the ASCII record separator, `0x1E`. One websocket frame
may hold several messages.

1. Open a websocket to `wss://www.switcheon.com/api/userhub`. The server accepts websocket
   clients that skip SignalR's negotiate request.
2. Send the handshake: `{"protocol":"json","version":1}␞`
3. Receive `{}␞`. Anything with an `error` field means the handshake was refused.
4. Register:
   `{"type":1,"invocationId":"1","target":"registerConnectionSecure","arguments":["<userIdBin>","<userSecret>"]}␞`
5. Receive a completion for it: `{"type":3,"invocationId":"1"}␞`
6. From then on, handle messages by `type`:

| `type` | Meaning | Action |
|---|---|---|
| `1` | Invocation. `target` is `updateFromServer`, `arguments[0]` is the update string | Parse and use it |
| `6` | Ping from the server | None needed |
| `7` | Close | Reconnect |

**Send `{"type":6}␞` every 15 seconds.** The server closes a connection it hasn't heard
from in 30 seconds, even while it is sending you updates.

## Things that will catch you out

- **`requestedStatus` sets every channel.** Leaving it out turns them all off.
- **`Success` means queued, not switched.** Watch for the check-in.
- **ICCIDs lose precision in JavaScript.** They are 20 digit JSON numbers, past what a
  JavaScript number holds exactly. The JavaScript client turns `iccid` into a string
  before parsing. IMEIs fit.
- **Wrong credentials look different on each call.** Login returns `401`, `GET /api/User`
  returns `404`, `PUT /api/BoxUser` returns `401`, and `PUT /api/req` returns `500`.
- **Adding a box that is already on the account returns `400`.**
- **The API is rate limited** at 100 requests a second per address, with a burst of 20.
  There is no need to poll quickly: the live connection tells you when anything changes.
- **Browsers are limited to SwitcheOn's own origins** by CORS. Call the API from a server
  or a script.
