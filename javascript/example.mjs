// Command line walkthrough of the client. Run with no arguments for usage.
//
//   export SWITCHEON_EMAIL=you@example.com SWITCHEON_PASSWORD=...
//   node example.mjs boxes
//   node example.mjs set 4129B3480C8D4C76888DEFAFD416458E 2 on
//   node example.mjs watch

import { SwitcheOnClient, DEFAULT_BASE_URL, boxIdBinFromText, channelIsOn } from "./switcheon.mjs";

const usage = `Usage: node example.mjs <command>

  boxes                        list the boxes on the account
  add <qr code text>           add a box from the text of its QR code
  set <box id> <channel> on|off  switch one channel, numbered from 1
  watch                        print live updates until Ctrl+C

Environment: SWITCHEON_EMAIL, SWITCHEON_PASSWORD, and optionally SWITCHEON_URL
(default ${DEFAULT_BASE_URL}).`;

function requireEnv(name) {
  const value = process.env[name];
  if (!value) {
    console.error(`${name} is not set.\n\n${usage}`);
    process.exit(2);
  }
  return value;
}

function describeChannels(status, count, names) {
  return Array.from({ length: count }, (_, i) => {
    const name = names?.[i] || `Channel ${i + 1}`;
    return `${name} ${channelIsOn(status, i + 1) ? "on" : "off"}`;
  }).join(", ");
}

const [command, ...args] = process.argv.slice(2);
if (!["boxes", "add", "set", "watch"].includes(command)) {
  console.log(usage);
  process.exit(command ? 2 : 0);
}

async function main() {
  const client = new SwitcheOnClient({ baseUrl: process.env.SWITCHEON_URL ?? DEFAULT_BASE_URL });
  await client.login(requireEnv("SWITCHEON_EMAIL"), requireEnv("SWITCHEON_PASSWORD"));

  if (command === "boxes") {
    const user = await client.getUser();
    console.log(`${user.firstname} ${user.lastname} <${user.email}>, ${user.boxes.length} box(es)`);
    for (const box of user.boxes) {
      console.log(`\n${box.boxIdText}  ${box.location || "(no location)"}  ${box.online ? "online" : "offline"}`);
      console.log(`  ${describeChannels(box.currentStatus, box.channels, box.channelNames)}`);
      if (box.pendingStatus != null && box.pendingStatus !== box.currentStatus) {
        console.log(`  requested: ${describeChannels(box.pendingStatus, box.channels, box.channelNames)}`);
      }
    }
  } else if (command === "add") {
    if (args.length !== 1) {
      console.error(usage);
      process.exit(2);
    }
    const message = await client.addBox(args[0]);
    console.log(message || "Box added.");
  } else if (command === "set") {
    const [boxId, channel, state] = args;
    if (!boxId || !channel || !["on", "off"].includes(state)) {
      console.error(usage);
      process.exit(2);
    }
    await client.setChannel(boxIdBinFromText(boxId), Number(channel), state === "on");
    console.log(`Requested channel ${channel} ${state}. The box applies it on its next check-in.`);
  } else if (command === "watch") {
    const connection = await client.connectLive(
      (update) => {
        const time = update.receivedAt.toLocaleTimeString();
        if (update.kind === "request") {
          console.log(`${time}  ${update.boxIdBin}  requested status ${update.requestedStatus}`);
        } else {
          console.log(`${time}  ${update.boxIdBin}  status ${update.status}  temp ${update.temperature}`);
        }
      },
      { onStateChange: (state) => console.log(`[${state}]`) },
    );
    console.log("Watching for updates. Ctrl+C to stop.");
    process.on("SIGINT", async () => {
      await connection.stop();
      process.exit(0);
    });
  }
}

main().catch((error) => {
  console.error(`Error: ${error.message}`);
  process.exit(1);
});
