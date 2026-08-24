using BambuMCPSharp.Configuration;
using FluentFTP;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace BambuMCPSharp.Services;

/// <summary>
/// SD-card access over the printer's implicit FTPS on port 990 (user <c>bblp</c>, password
/// the access code). Sessions are opened per call and closed immediately: the printer's FTP
/// daemon is single-user-grade and long-held control connections go stale mid-print.
///
/// The daemon also demands TLS session reuse on data connections; FluentFTP plus the
/// platform TLS stacks (SChannel, OpenSSL on .NET 8+) resume transparently.
/// </summary>
public sealed class BambuFtp
{
    private readonly BambuOptions _options;

    public BambuFtp(IOptions<BambuOptions> options) => _options = options.Value;

    /// <summary>Run one FTPS operation inside a fresh, fully configured session.</summary>
    public async Task<T> WithClientAsync<T>(
        BambuPrinterEntry printer,
        Func<AsyncFtpClient, Task<T>> operation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(printer.Host) || string.IsNullOrWhiteSpace(printer.AccessCode))
        {
            throw new McpException(
                $"Printer '{printer.Alias}' is missing Host or AccessCode — both are needed for FTPS.");
        }

        var timeoutMs = Math.Max(1, _options.FtpTimeoutSeconds) * 1000;
        var client = new AsyncFtpClient(printer.Host, "bblp", printer.AccessCode, 990);
        client.Config.EncryptionMode = FtpEncryptionMode.Implicit;
        client.Config.ValidateAnyCertificate = true; // self-signed printer certificate; the access code authenticates
        client.Config.DataConnectionType = FtpDataConnectionType.PASV;
        client.Config.ConnectTimeout = timeoutMs;
        client.Config.ReadTimeout = timeoutMs;
        client.Config.DataConnectionConnectTimeout = timeoutMs;
        client.Config.DataConnectionReadTimeout = timeoutMs;

        try
        {
            await client.Connect(ct);
            return await operation(client);
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            throw new McpException(
                $"FTPS operation against '{printer.Alias}' ({printer.Host}:990) failed: {Root(ex).Message}. " +
                "Check the printer is on this LAN with LAN mode enabled and the access code is current.");
        }
        finally
        {
            try
            {
                await client.Disconnect(CancellationToken.None);
            }
            catch
            {
                // The printer often drops the control connection first; that is not an error.
            }
            client.Dispose();
        }
    }

    /// <summary>
    /// Normalize an agent-supplied remote path: forward slashes, always rooted, no traversal.
    /// </summary>
    public static string NormalizeRemotePath(string? path)
    {
        var cleaned = (path ?? "/").Trim().Replace('\\', '/');
        if (!cleaned.StartsWith('/')) cleaned = "/" + cleaned;
        if (cleaned.Contains("..", StringComparison.Ordinal))
        {
            throw new McpException($"Remote path '{path}' is not allowed: no '..' segments.");
        }
        return cleaned;
    }

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }
}
