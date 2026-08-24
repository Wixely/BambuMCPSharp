using BambuLab.X1Camera;
using BambuLab.X1Camera.Imaging;
using BambuMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace BambuMCPSharp.Services;

/// <summary>One decoded camera still, plus enough metadata to reason about it.</summary>
public sealed record CameraSnapshot(byte[] PngData, int Width, int Height, TimeSpan CaptureDuration);

/// <summary>
/// Snapshots from the X1C's authenticated RTSPS stream (port 322, "LAN Mode Liveview").
/// Each capture opens a fresh session, waits for the next keyframe, decodes it in pure
/// managed code (BambuLab.X1Camera.Imaging → H264Sharp.Decoder), and tears the session
/// down. A snapshot every few seconds is what a CV watch-loop needs; a persistent stream
/// buys nothing here and a bounded per-capture session cannot leak.
/// </summary>
public sealed class CameraService
{
    private readonly BambuOptions _options;

    public CameraService(IOptions<BambuOptions> options) => _options = options.Value;

    public async Task<CameraSnapshot> SnapshotAsync(BambuPrinterEntry printer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(printer.Host) || string.IsNullOrWhiteSpace(printer.AccessCode))
        {
            throw new McpException(
                $"Printer '{printer.Alias}' is missing Host or AccessCode — both are needed for the camera.");
        }

        var pinned = string.IsNullOrWhiteSpace(printer.CameraCertificateSha256)
            ? null
            : printer.CameraCertificateSha256.Trim();

        var cameraOptions = new X1CameraOptions(printer.Host, printer.AccessCode)
        {
            Port = printer.CameraPort,
            PinnedCertificateSha256 = pinned,
            // LAN-mode printers present a self-signed certificate; without a configured pin
            // the certificate is accepted, matching the MQTT and FTPS channels.
            AllowUntrustedCertificate = pinned is null,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.CameraTimeoutSeconds)),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.CameraTimeoutSeconds)));

        var started = DateTimeOffset.UtcNow;
        try
        {
            await using var session = await new X1CameraClient().ConnectAsync(cameraOptions, timeout.Token);
            var image = await session.CapturePngAsync(timeout.Token);
            return new CameraSnapshot(
                image.Data.ToArray(),
                image.Width,
                image.Height,
                DateTimeOffset.UtcNow - started);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException(
                $"Camera capture from '{printer.Alias}' ({printer.Host}:{printer.CameraPort}) timed out after " +
                $"{_options.CameraTimeoutSeconds}s. Check: is \"LAN Mode Liveview\" enabled on the printer? " +
                "Is port 322 reachable? Is the access code current?");
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            throw new McpException(
                $"Camera capture from '{printer.Alias}' ({printer.Host}:{printer.CameraPort}) failed: {Root(ex).Message}. " +
                "Check: is \"LAN Mode Liveview\" enabled on the printer? Is the access code current?");
        }
    }

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }
}
