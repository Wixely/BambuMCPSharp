using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuMCPSharp.Configuration;
using ModelContextProtocol;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Serilog;

namespace BambuMCPSharp.Services;

/// <summary>
/// Outcome of one command sent to the printer. The printer acknowledges by echoing the
/// command's <c>sequence_id</c> on the report topic; no echo within the timeout does NOT
/// mean the command failed — firmware executes first and answers when it feels like it —
/// so the two cases are reported distinctly instead of collapsed into an error.
/// </summary>
public sealed record CommandResult(bool Acknowledged, JsonObject? Ack, string SequenceId)
{
    public string Outcome => Acknowledged ? "acknowledged" : "sent, not acknowledged in time — verify with bambu_status";
}

/// <summary>
/// The MQTT half of one printer: connection, subscription to <c>device/{serial}/report</c>,
/// a merged state cache, and sequence-id command correlation.
///
/// Connection is on demand: the first tool call connects, the connection then stays up and
/// the X1C streams state into the cache (a full object roughly every second while printing).
/// If the printer drops the link, nothing retries in the background — the next tool call
/// reconnects. That way a powered-off printer costs nothing and an agent's watch loop keeps
/// the link warm by construction.
///
/// The cache deep-merges every report section by section. The X1C usually pushes complete
/// objects so merging looks redundant, but the P1/A1 push diffs, and even the X1C sends
/// bare command acks — merging keeps the cache correct for all of them.
/// </summary>
public sealed class PrinterConnection : IAsyncDisposable
{
    private readonly BambuPrinterEntry _printer;
    private readonly BambuOptions _options;
    private readonly Serilog.ILogger _log;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();

    private IMqttClient? _client;
    private JsonObject _state = new();
    private long _reportCount;
    private int _sequenceId;

    public PrinterConnection(BambuPrinterEntry printer, BambuOptions options)
    {
        _printer = printer;
        _options = options;
        _log = Log.ForContext("SourceContext", $"BambuMCPSharp.Printer.{printer.Alias}");
    }

    public BambuPrinterEntry Printer => _printer;
    public bool IsConnected => _client?.IsConnected == true;
    public DateTimeOffset? LastReportUtc { get; private set; }
    public long ReportCount => Interlocked.Read(ref _reportCount);

    private string ReportTopic => $"device/{_printer.SerialNumber}/report";
    private string RequestTopic => $"device/{_printer.SerialNumber}/request";

    // ---------------------------------------------------------------- connection

    /// <summary>Connect and subscribe if not already connected. Serialized; safe to call from every tool.</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_printer.Host) ||
            string.IsNullOrWhiteSpace(_printer.SerialNumber) ||
            string.IsNullOrWhiteSpace(_printer.AccessCode))
        {
            throw new McpException(
                $"Printer '{_printer.Alias}' is not fully configured. Bambu:Printers entries need " +
                "Host, SerialNumber, and AccessCode (all three are on the printer's screen).");
        }

        if (IsConnected) return;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return;

            if (_client is not null)
            {
                _client.Dispose();
                _client = null;
            }

            var client = new MqttClientFactory().CreateMqttClient();
            client.ApplicationMessageReceivedAsync += OnReportAsync;

            var mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(_printer.Host, 8883)
                .WithCredentials("bblp", _printer.AccessCode)
                .WithClientId($"BambuMCPSharp-{Guid.NewGuid():N}"[..23])
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithCleanSession()
                .WithTlsOptions(tls => tls
                    .UseTls()
                    .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)
                    // The printer presents a self-signed certificate from Bambu's private CA.
                    // The channel is still authenticated: nothing works without the access code.
                    .WithCertificateValidationHandler(_ => true))
                .Build();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.MqttConnectTimeoutSeconds)));

            try
            {
                await client.ConnectAsync(mqttOptions, timeout.Token);
                await client.SubscribeAsync(
                    new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(ReportTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce))
                        .Build(),
                    timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                client.Dispose();
                throw new McpException(
                    $"Cannot reach printer '{_printer.Alias}' at {_printer.Host}:8883 (MQTT over TLS): {Root(ex).Message}. " +
                    "Check the printer is powered on, on this LAN, in LAN mode, and that the access code is current " +
                    "(it changes when LAN mode is toggled).");
            }

            _client = client;
            _log.Information("Connected to {Host}:8883 and subscribed to reports", _printer.Host);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private Task OnReportAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
            if (JsonNode.Parse(payload) is not JsonObject report) return Task.CompletedTask;

            lock (_stateLock)
            {
                MergeInto(_state, report);
                LastReportUtc = DateTimeOffset.UtcNow;
            }
            Interlocked.Increment(ref _reportCount);

            // Complete any command waiting on an echoed sequence id.
            foreach (var (_, sectionNode) in report)
            {
                if (sectionNode is not JsonObject section) continue;
                var seq = section["sequence_id"]?.GetValue<string>();
                if (seq is not null && _pending.TryRemove(seq, out var tcs))
                {
                    tcs.TrySetResult((JsonObject)section.DeepClone());
                }
            }
        }
        catch (JsonException)
        {
            // Firmware occasionally emits noise; a bad payload must not kill the receive pump.
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error processing printer report");
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- state

    /// <summary>
    /// Snapshot of the merged state, refreshed first when forced or older than
    /// <see cref="BambuOptions.StateFreshSeconds"/> (a <c>pushall</c> is requested and the
    /// next report awaited, bounded). Returns whatever is cached if the refresh times out —
    /// staleness is reported, not thrown, so a monitoring loop keeps working through blips.
    /// </summary>
    public async Task<(JsonObject State, DateTimeOffset? ReportedUtc)> GetStateAsync(
        CancellationToken ct,
        bool forceRefresh = false)
    {
        await EnsureConnectedAsync(ct);

        var maxAge = TimeSpan.FromSeconds(Math.Max(1, _options.StateFreshSeconds));
        if (!forceRefresh && LastReportUtc is { } last && DateTimeOffset.UtcNow - last < maxAge)
        {
            return (SnapshotState(), LastReportUtc);
        }

        var before = ReportCount;
        await PublishAsync("pushing", new JsonObject { ["command"] = "pushall", ["version"] = 1, ["push_target"] = 1 }, ct);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(1, _options.CommandAckTimeoutSeconds));
        while (ReportCount == before && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
        }

        return (SnapshotState(), LastReportUtc);
    }

    private JsonObject SnapshotState()
    {
        lock (_stateLock)
        {
            return (JsonObject)_state.DeepClone();
        }
    }

    // ---------------------------------------------------------------- commands

    /// <summary>
    /// Send one command envelope (<c>{"<paramref name="section"/>": {body + sequence_id}}</c>)
    /// and wait (bounded) for the echoed sequence id.
    /// </summary>
    public async Task<CommandResult> SendAsync(string section, JsonObject body, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var seq = Interlocked.Increment(ref _sequenceId).ToString();
        body["sequence_id"] = seq;

        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;

        try
        {
            await PublishAsync(section, body, ct);

            var ackTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.CommandAckTimeoutSeconds));
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(ackTimeout, ct));
            if (winner == tcs.Task)
            {
                return new CommandResult(true, await tcs.Task, seq);
            }

            return new CommandResult(false, null, seq);
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    private async Task PublishAsync(string section, JsonObject body, CancellationToken ct)
    {
        var envelope = new JsonObject { [section] = body.DeepClone() };
        var client = _client ?? throw new McpException($"Printer '{_printer.Alias}' is not connected.");
        await client.PublishStringAsync(
            RequestTopic,
            envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            MqttQualityOfServiceLevel.AtMostOnce,
            retain: false,
            cancellationToken: ct);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Deep-merge: objects recurse, everything else (including arrays) replaces.</summary>
    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceObject && target[key] is JsonObject targetObject)
            {
                MergeInto(targetObject, sourceObject);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    public async ValueTask DisposeAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync();
                }
            }
            catch
            {
                // Shutdown must not throw over a printer that already went away.
            }
            client.Dispose();
        }
        _connectLock.Dispose();
    }
}
