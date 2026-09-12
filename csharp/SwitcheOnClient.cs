// SwitcheOn API client for .NET 10.
//
// Covers what an integration needs: log in with the email and password from the
// phone app, read the account and its boxes, add a box from its QR code, switch
// channels, and receive live updates as boxes check in.
//
// See ../README.md for the API itself. This file is meant to be read as well as used.

using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;

namespace SwitcheOn;

public sealed class SwitcheOnException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed class SwitcheOnClient : IDisposable
{
    public const string DefaultBaseUrl = "https://www.switcheon.com";

    // Tokens last eight hours. Renew a little early so a request never goes out
    // with a token that expires in flight.
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? email;
    private string? password;
    private string? token;
    private DateTimeOffset tokenExpires;

    public SwitcheOnClient(string baseUrl = DefaultBaseUrl, HttpClient? httpClient = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string BaseUrl { get; }

    /// <summary>The logged in account's id, set by <see cref="LoginAsync"/>.</summary>
    public string? UserIdBin { get; private set; }

    /// <summary>
    /// A fresh random user id, for registering an account directly through the API
    /// rather than through the phone app. Every id is 16 bytes sent as standard base64.
    /// </summary>
    public static string NewUserIdBin() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Turns anything that identifies a box into the base64 id the API expects: the
    /// text of the QR code on the box, or the 32 hex digit BoxIdText, with or without dashes.
    /// </summary>
    public static string BoxIdBinFromText(string text)
    {
        var hex = text.Trim().Split('/')[^1].Replace("-", "");
        if (!Regex.IsMatch(hex, "^[0-9a-fA-F]{32}$"))
        {
            throw new ArgumentException($"Not a SwitcheOn QR code or box id: {text}", nameof(text));
        }
        return Convert.ToBase64String(Convert.FromHexString(hex));
    }

    /// <summary>Whether a 1-based channel is on in a status bitmask.</summary>
    public static bool ChannelIsOn(int status, int channel) => ((status >> (channel - 1)) & 1) == 1;

    /// <summary>A status bitmask with one 1-based channel switched on or off.</summary>
    public static int WithChannel(int status, int channel, bool on) =>
        on ? status | (1 << (channel - 1)) : status & ~(1 << (channel - 1));

    /// <summary>
    /// Logs in with the email and password set in the phone app. The credentials are
    /// kept in memory so the token can be renewed before it expires.
    /// </summary>
    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        // Form encoded, not JSON: a JSON body is ignored and comes back as a 401
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["UserPassword"] = password,
        });
        using var res = await http.PostAsync($"{BaseUrl}/api/User", form, cancellationToken);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SwitcheOnException("The email or password was not accepted", res.StatusCode);
        }
        await EnsureSuccess(res, "Login");

        var body = await res.Content.ReadFromJsonAsync<LoginResponse>(Json, cancellationToken)
            ?? throw new SwitcheOnException("Login returned an empty response");
        this.email = email;
        this.password = password;
        UserIdBin = body.UserIdBin;
        token = body.Token;
        tokenExpires = TokenExpiry(body.Token);
    }

    /// <summary>The account and every box on it.</summary>
    public async Task<SwitcheOnUser> GetUserAsync(CancellationToken cancellationToken = default)
    {
        var query = $"UserIdBin={Uri.EscapeDataString(RequireUser())}" +
                    $"&UserSecret={Uri.EscapeDataString(await SecretAsync(cancellationToken))}";
        using var res = await http.GetAsync($"{BaseUrl}/api/User?{query}", cancellationToken);
        // A rejected token comes back as 404, not 401
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SwitcheOnException("The account was not found or the token was rejected", res.StatusCode);
        }
        await EnsureSuccess(res, "GET /api/User");
        return await res.Content.ReadFromJsonAsync<SwitcheOnUser>(Json, cancellationToken)
            ?? throw new SwitcheOnException("GET /api/User returned an empty response");
    }

    /// <summary>
    /// Adds a box to the account from its QR code or box id. The first account on a
    /// box becomes its owner, and adding a box that has never been activated starts
    /// its cellular activation. Returns the server's message, which may be empty.
    /// </summary>
    public Task<string> AddBoxAsync(string qrOrBoxId, CancellationToken cancellationToken = default) =>
        PutAsync("/api/BoxUser", new Dictionary<string, object?> { ["boxIdBin"] = BoxIdBinFromText(qrOrBoxId) }, cancellationToken);

    /// <summary>
    /// Asks a box to set every channel at once. Bit 0 is channel 1. This is the whole
    /// state, not a toggle, and bits beyond the box's channel count are dropped.
    /// Completes once the request is queued; the box applies it on its next exchange
    /// with the server, and the change shows up as a live update and in CurrentStatus.
    /// </summary>
    public async Task SetStatusAsync(string boxIdBin, int status, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(status);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(status, 255);
        await PutAsync("/api/req", new Dictionary<string, object?>
        {
            ["boxIdBin"] = boxIdBin,
            ["requestedStatus"] = status,
        }, cancellationToken);
    }

    /// <summary>
    /// Switches one 1-based channel and leaves the others as they were. "As they were"
    /// means the most recent request if one is still pending, otherwise what the box
    /// last reported, so two quick calls don't undo each other.
    /// </summary>
    public async Task SetChannelAsync(string boxIdBin, int channel, bool on, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(cancellationToken);
        var box = user.Boxes.FirstOrDefault(b => b.BoxIdBin == boxIdBin)
            ?? throw new SwitcheOnException($"Box {boxIdBin} is not on this account");
        if (channel < 1 || channel > box.Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), $"Box has channels 1 to {box.Channels}, got {channel}");
        }
        var current = box.PendingStatus ?? box.CurrentStatus;
        await SetStatusAsync(boxIdBin, WithChannel(current, channel, on), cancellationToken);
    }

    /// <summary>
    /// Opens the live update connection. onUpdate receives a <see cref="LiveUpdate"/>
    /// each time a box on the account checks in or someone sends it a request.
    /// Reconnects on its own, forever, until the returned connection is disposed.
    /// </summary>
    public async Task<HubConnection> ConnectLiveAsync(
        Action<LiveUpdate> onUpdate,
        Action<string>? onStateChange = null,
        CancellationToken cancellationToken = default)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{BaseUrl}/api/userhub")
            .WithAutomaticReconnect(new RetryForever())
            .Build();

        connection.On<string>("updateFromServer", message => onUpdate(LiveUpdate.FromMessage(message)));

        // The server forgets a connection when it drops, so every reconnect registers again
        connection.Reconnecting += _ =>
        {
            onStateChange?.Invoke("reconnecting");
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            await RegisterAsync(connection, CancellationToken.None);
            onStateChange?.Invoke("connected");
        };
        connection.Closed += _ =>
        {
            onStateChange?.Invoke("closed");
            return Task.CompletedTask;
        };

        await connection.StartAsync(cancellationToken);
        await RegisterAsync(connection, cancellationToken);
        onStateChange?.Invoke("connected");
        return connection;
    }

    public void Dispose()
    {
        tokenLock.Dispose();
        if (ownsHttp)
        {
            http.Dispose();
        }
    }

    private async Task RegisterAsync(HubConnection connection, CancellationToken cancellationToken) =>
        await connection.InvokeAsync("registerConnectionSecure", RequireUser(), await SecretAsync(cancellationToken), cancellationToken);

    private string RequireUser() => UserIdBin ?? throw new InvalidOperationException("Call LoginAsync first");

    private async Task<string> SecretAsync(CancellationToken cancellationToken)
    {
        if (token is null)
        {
            throw new InvalidOperationException("Call LoginAsync first");
        }
        if (DateTimeOffset.UtcNow < tokenExpires - TokenRefreshMargin)
        {
            return token;
        }

        // Live update reconnects and REST calls can race to renew; only one needs to
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow >= tokenExpires - TokenRefreshMargin)
            {
                await LoginAsync(email!, password!, cancellationToken);
            }
            return token;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private async Task<string> PutAsync(string path, Dictionary<string, object?> fields, CancellationToken cancellationToken)
    {
        fields["userIdBin"] = RequireUser();
        fields["userSecret"] = await SecretAsync(cancellationToken);
        using var res = await http.PutAsJsonAsync($"{BaseUrl}{path}", fields, Json, cancellationToken);
        await EnsureSuccess(res, $"PUT {path}");
        return await res.Content.ReadAsStringAsync(cancellationToken);
    }

    private static Task EnsureSuccess(HttpResponseMessage res, string what) =>
        res.IsSuccessStatusCode
            ? Task.CompletedTask
            : throw new SwitcheOnException($"{what} failed with HTTP {(int)res.StatusCode}", res.StatusCode);

    // The token is "jwt:" followed by a standard JWT. Only the expiry is read here;
    // the server is what checks the signature.
    private static DateTimeOffset TokenExpiry(string jwt)
    {
        var payload = jwt["jwt:".Length..].Split('.')[1];
        using var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(payload));
        return DateTimeOffset.FromUnixTimeSeconds(claims.RootElement.GetProperty("exp").GetInt64());
    }

    private sealed class RetryForever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, context.PreviousRetryCount)));
    }

    private sealed record LoginResponse(string UserIdBin, string Token);
}

public sealed record SwitcheOnUser(
    string? Firstname,
    string? Lastname,
    string? Email,
    string? BillingStatus,
    IReadOnlyList<SwitcheOnBox> Boxes);

/// <summary>
/// The fields of a box most integrations use. GET /api/User returns more; add
/// properties here to pick them up.
/// </summary>
public sealed record SwitcheOnBox(
    string BoxIdBin,
    string BoxIdText,
    ulong Imei,
    decimal Iccid,
    string? Location,
    string? DeviceType,
    int Channels,
    int ChannelsExclusive,
    IReadOnlyList<string>? ChannelNames,
    int CurrentStatus,
    int? PendingStatus,
    byte? Online,
    float? Temp,
    bool? Celcius,
    int? SignalStrength,
    DateTime? PaidUntil,
    DateTime? Updated,
    SwitcheOnOperator? Owner,
    IReadOnlyList<SwitcheOnOperator>? Users);

public sealed record SwitcheOnOperator(string UserIdBin, bool Owner, string? Firstname, string? Lastname);

/// <summary>
/// One live update, normalised. The server sends each update as a JSON string, and
/// only the short lowercase fields carry information: the rest are defaults that would
/// look like real values (CurrentStatus 0, Channels 0) if merged into a box.
/// </summary>
public sealed record LiveUpdate(
    string BoxIdBin,
    LiveUpdateKind Kind,
    DateTimeOffset ReceivedAt,
    int? Status,
    int? RequestedStatus,
    int? Exclusive,
    float? Temperature,
    uint? Sequence,
    IReadOnlyList<int>? Analog,
    string Raw)
{
    public static LiveUpdate FromMessage(string message)
    {
        var wire = JsonSerializer.Deserialize<WireUpdate>(message)
            ?? throw new SwitcheOnException("Live update was empty");
        return new LiveUpdate(
            wire.BoxIdBin,
            // A request echo always carries req; a check-in from the box never does
            wire.Req is not null ? LiveUpdateKind.Request : LiveUpdateKind.Checkin,
            // Box updates carry no timestamp of their own
            DateTimeOffset.UtcNow,
            wire.Stat,
            wire.Req,
            wire.Excl,
            wire.Temp,
            wire.Seq,
            wire.Anlg,
            message);
    }

    // The update arrives with both "temp" and "Temp", so names are matched exactly
    private sealed record WireUpdate(
        [property: JsonPropertyName("BoxIdBin")] string BoxIdBin,
        [property: JsonPropertyName("stat")] int? Stat,
        [property: JsonPropertyName("req")] int? Req,
        [property: JsonPropertyName("excl")] int? Excl,
        [property: JsonPropertyName("temp")] float? Temp,
        [property: JsonPropertyName("seq")] uint? Seq,
        [property: JsonPropertyName("anlg")] List<int>? Anlg);
}

public enum LiveUpdateKind
{
    /// <summary>The box reported in: Status and Temperature are what it has now.</summary>
    Checkin,

    /// <summary>Someone asked the box to switch: RequestedStatus is what they asked for.</summary>
    Request,
}
