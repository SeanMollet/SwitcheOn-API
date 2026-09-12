// SwitcheOn API client for Node.js 18 or later.
//
// Covers what an integration needs: log in with the email and password from the
// phone app, read the account and its boxes, add a box from its QR code, switch
// channels, and receive live updates as boxes check in.
//
// See ../README.md for the API itself. This file is meant to be read as well as used.

import { randomBytes } from "node:crypto";
import * as signalR from "@microsoft/signalr";

export const DEFAULT_BASE_URL = "https://www.switcheon.com";

// Tokens last eight hours. Renew a little early so a request never goes out with
// a token that expires in flight.
const TOKEN_REFRESH_MARGIN_SECONDS = 300;

export class SwitcheOnError extends Error {
  constructor(message, status) {
    super(message);
    this.name = "SwitcheOnError";
    this.status = status;
  }
}

/**
 * A fresh random user id, for registering an account directly through the API
 * rather than through the phone app. Every id is 16 bytes sent as standard base64.
 */
export function newUserIdBin() {
  return randomBytes(16).toString("base64");
}

/**
 * Turns anything that identifies a box into the base64 id the API expects: the
 * text of the QR code on the box, or the 32 hex digit BoxIdText, with or without dashes.
 */
export function boxIdBinFromText(text) {
  const hex = String(text).trim().split("/").pop().replace(/-/g, "");
  if (!/^[0-9a-fA-F]{32}$/.test(hex)) {
    throw new Error(`Not a SwitcheOn QR code or box id: ${text}`);
  }
  return Buffer.from(hex, "hex").toString("base64");
}

/** Whether a 1-based channel is on in a status bitmask. */
export function channelIsOn(status, channel) {
  return ((status >> (channel - 1)) & 1) === 1;
}

/** A status bitmask with one 1-based channel switched on or off. */
export function withChannel(status, channel, on) {
  const bit = 1 << (channel - 1);
  return on ? status | bit : status & ~bit;
}

export class SwitcheOnClient {
  #email = null;
  #password = null;
  #token = null;
  #tokenExpires = 0;

  constructor({ baseUrl = DEFAULT_BASE_URL } = {}) {
    this.baseUrl = baseUrl.replace(/\/+$/, "");
    /** The logged in account's id, set by login(). */
    this.userIdBin = null;
  }

  /**
   * Logs in with the email and password set in the phone app. The credentials are
   * kept in memory so the token can be renewed before it expires.
   */
  async login(email, password) {
    // Form encoded, not JSON: a JSON body is ignored and comes back as a 401
    const res = await fetch(`${this.baseUrl}/api/User`, {
      method: "POST",
      body: new URLSearchParams({ Email: email, UserPassword: password }),
    });
    if (res.status === 401) {
      throw new SwitcheOnError("The email or password was not accepted", 401);
    }
    if (!res.ok) {
      throw new SwitcheOnError(`Login failed with HTTP ${res.status}`, res.status);
    }

    const body = await res.json();
    this.#email = email;
    this.#password = password;
    this.userIdBin = body.userIdBin;
    this.#token = body.token;
    this.#tokenExpires = tokenExpiry(body.token);
  }

  /** The account and every box on it. */
  async getUser() {
    const params = new URLSearchParams({ UserIdBin: this.#requireUser(), UserSecret: await this.#secret() });
    const res = await fetch(`${this.baseUrl}/api/User?${params}`);
    // A rejected token comes back as 404, not 401
    if (res.status === 404) {
      throw new SwitcheOnError("The account was not found or the token was rejected", 404);
    }
    if (!res.ok) {
      throw new SwitcheOnError(`GET /api/User failed with HTTP ${res.status}`, res.status);
    }
    return parseKeepingIccid(await res.text());
  }

  /**
   * Adds a box to the account from its QR code or box id. The first account on a
   * box becomes its owner, and adding a box that has never been activated starts
   * its cellular activation. Returns the server's message, which may be empty.
   */
  async addBox(qrOrBoxId) {
    return this.#put("/api/BoxUser", { boxIdBin: boxIdBinFromText(qrOrBoxId) });
  }

  /**
   * Asks a box to set every channel at once. Bit 0 is channel 1. This is the whole
   * state, not a toggle, and bits beyond the box's channel count are dropped.
   *
   * Resolves once the request is queued. The box applies it on its next exchange
   * with the server, and the change shows up as a live update and in currentStatus.
   */
  async setStatus(boxIdBin, status) {
    if (!Number.isInteger(status) || status < 0 || status > 255) {
      throw new RangeError(`Status must be a bitmask from 0 to 255, got ${status}`);
    }
    await this.#put("/api/req", { boxIdBin, requestedStatus: status });
  }

  /**
   * Switches one 1-based channel and leaves the others as they were. "As they were"
   * means the most recent request if one is still pending, otherwise what the box
   * last reported, so two quick calls don't undo each other.
   */
  async setChannel(boxIdBin, channel, on) {
    const box = (await this.getUser()).boxes.find((b) => b.boxIdBin === boxIdBin);
    if (!box) {
      throw new Error(`Box ${boxIdBin} is not on this account`);
    }
    if (!Number.isInteger(channel) || channel < 1 || channel > box.channels) {
      throw new RangeError(`Box has channels 1 to ${box.channels}, got ${channel}`);
    }
    const current = box.pendingStatus ?? box.currentStatus;
    await this.setStatus(boxIdBin, withChannel(current, channel, on));
  }

  /**
   * Opens the live update connection. onUpdate receives a LiveUpdate (see
   * toLiveUpdate) each time a box on the account checks in or someone sends it a
   * request. Reconnects on its own, forever, until you call stop() on the result.
   */
  async connectLive(onUpdate, { onStateChange = () => {} } = {}) {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${this.baseUrl}/api/userhub`)
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) => Math.min(30_000, 1_000 * 2 ** context.previousRetryCount),
      })
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    connection.on("updateFromServer", (message) => onUpdate(toLiveUpdate(message)));

    const register = async () =>
      connection.invoke("registerConnectionSecure", this.#requireUser(), await this.#secret());

    // The server forgets a connection when it drops, so every reconnect registers again
    connection.onreconnecting(() => onStateChange("reconnecting"));
    connection.onreconnected(async () => {
      await register();
      onStateChange("connected");
    });
    connection.onclose(() => onStateChange("closed"));

    await connection.start();
    await register();
    onStateChange("connected");
    return connection;
  }

  #requireUser() {
    if (!this.userIdBin) {
      throw new Error("Call login() first");
    }
    return this.userIdBin;
  }

  async #secret() {
    if (!this.#token) {
      throw new Error("Call login() first");
    }
    if (Date.now() / 1000 > this.#tokenExpires - TOKEN_REFRESH_MARGIN_SECONDS) {
      await this.login(this.#email, this.#password);
    }
    return this.#token;
  }

  async #put(path, fields) {
    const res = await fetch(`${this.baseUrl}${path}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ userIdBin: this.#requireUser(), userSecret: await this.#secret(), ...fields }),
    });
    const text = await res.text();
    if (!res.ok) {
      throw new SwitcheOnError(`PUT ${path} failed with HTTP ${res.status}`, res.status);
    }
    return text;
  }
}

/**
 * Normalises a live update. The server sends each one as a JSON string, and only
 * the short lowercase fields carry information: the rest are defaults that would
 * look like real values (currentStatus 0, channels 0) if merged into a box.
 */
export function toLiveUpdate(message) {
  const raw = typeof message === "string" ? JSON.parse(message) : message;
  return {
    boxIdBin: raw.BoxIdBin,
    // A request echo always carries req; a check-in from the box never does
    kind: raw.req != null ? "request" : "checkin",
    // Box updates carry no timestamp of their own
    receivedAt: new Date(),
    status: raw.stat ?? null,
    requestedStatus: raw.req ?? null,
    exclusive: raw.excl ?? null,
    temperature: raw.temp ?? null,
    sequence: raw.seq ?? null,
    analog: raw.anlg ?? null,
    log: raw.log ?? null,
    raw,
  };
}

// The token is "jwt:" followed by a standard JWT. Only the expiry is read here;
// the server is what checks the signature.
function tokenExpiry(token) {
  const payload = token.replace(/^jwt:/, "").split(".")[1];
  return JSON.parse(Buffer.from(payload, "base64url").toString("utf8")).exp;
}

// ICCIDs are 20 digit numbers, past what a JavaScript number holds exactly, so
// they are turned into strings before parsing rather than silently rounded.
function parseKeepingIccid(text) {
  return JSON.parse(text.replace(/"iccid":\s*(\d+)/g, '"iccid":"$1"'));
}
