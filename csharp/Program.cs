// Command line walkthrough of the client. Run with no arguments for usage.
//
//   export SWITCHEON_EMAIL=you@example.com SWITCHEON_PASSWORD=...
//   dotnet run -- boxes
//   dotnet run -- set 4129B3480C8D4C76888DEFAFD416458E 2 on
//   dotnet run -- watch

using SwitcheOn;

var usage = $"""
    Usage: dotnet run -- <command>

      boxes                          list the boxes on the account
      add <qr code text>             add a box from the text of its QR code
      set <box id> <channel> on|off  switch one channel, numbered from 1
      watch                          print live updates until Ctrl+C

    Environment: SWITCHEON_EMAIL, SWITCHEON_PASSWORD, and optionally SWITCHEON_URL
    (default {SwitcheOnClient.DefaultBaseUrl}).
    """;

string[] commands = ["boxes", "add", "set", "watch"];
if (args.Length == 0 || !commands.Contains(args[0]))
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 0 : 2;
}

string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is not set.\n\n{usage}");

string DescribeChannels(int status, int count, IReadOnlyList<string>? names) =>
    string.Join(", ", Enumerable.Range(1, count).Select(channel =>
    {
        var name = names is not null && names.Count >= channel && names[channel - 1] is { Length: > 0 } n ? n : $"Channel {channel}";
        return $"{name} {(SwitcheOnClient.ChannelIsOn(status, channel) ? "on" : "off")}";
    }));

try
{
    using var client = new SwitcheOnClient(Environment.GetEnvironmentVariable("SWITCHEON_URL") ?? SwitcheOnClient.DefaultBaseUrl);
    await client.LoginAsync(RequireEnv("SWITCHEON_EMAIL"), RequireEnv("SWITCHEON_PASSWORD"));

    switch (args[0])
    {
        case "boxes":
        {
            var user = await client.GetUserAsync();
            Console.WriteLine($"{user.Firstname} {user.Lastname} <{user.Email}>, {user.Boxes.Count} box(es)");
            foreach (var box in user.Boxes)
            {
                Console.WriteLine($"\n{box.BoxIdText}  {(string.IsNullOrEmpty(box.Location) ? "(no location)" : box.Location)}  {(box.Online > 0 ? "online" : "offline")}");
                Console.WriteLine($"  {DescribeChannels(box.CurrentStatus, box.Channels, box.ChannelNames)}");
                if (box.PendingStatus is { } pending && pending != box.CurrentStatus)
                {
                    Console.WriteLine($"  requested: {DescribeChannels(pending, box.Channels, box.ChannelNames)}");
                }
            }
            break;
        }

        case "add":
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine(usage);
                return 2;
            }
            var message = await client.AddBoxAsync(args[1]);
            Console.WriteLine(string.IsNullOrEmpty(message) ? "Box added." : message);
            break;
        }

        case "set":
        {
            if (args.Length != 4 || !int.TryParse(args[2], out var channel) || args[3] is not ("on" or "off"))
            {
                Console.Error.WriteLine(usage);
                return 2;
            }
            await client.SetChannelAsync(SwitcheOnClient.BoxIdBinFromText(args[1]), channel, args[3] == "on");
            Console.WriteLine($"Requested channel {channel} {args[3]}. The box applies it on its next check-in.");
            break;
        }

        case "watch":
        {
            var stop = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.TrySetResult();
            };

            await using var connection = await client.ConnectLiveAsync(
                update =>
                {
                    var time = update.ReceivedAt.ToLocalTime().ToString("HH:mm:ss");
                    Console.WriteLine(update.Kind == LiveUpdateKind.Request
                        ? $"{time}  {update.BoxIdBin}  requested status {update.RequestedStatus}"
                        : $"{time}  {update.BoxIdBin}  status {update.Status}  temp {update.Temperature}");
                },
                state => Console.WriteLine($"[{state}]"));

            Console.WriteLine("Watching for updates. Ctrl+C to stop.");
            await stop.Task;
            break;
        }
    }
    return 0;
}
catch (Exception error) when (error is SwitcheOnException or ArgumentException or InvalidOperationException or HttpRequestException)
{
    Console.Error.WriteLine($"Error: {error.Message}");
    return 1;
}
