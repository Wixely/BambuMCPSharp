using System.Collections.Concurrent;
using BambuMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace BambuMCPSharp.Services;

/// <summary>
/// Maps an alias to a <see cref="PrinterConnection"/>. Most deployments configure the one
/// X1C and never pass an alias; the registry exists so a print farm later is config, not
/// surgery.
/// </summary>
public sealed class PrinterRegistry : IAsyncDisposable
{
    private readonly BambuOptions _options;
    private readonly Dictionary<string, BambuPrinterEntry> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<PrinterConnection>> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _defaultAlias;
    private bool _disposed;

    public PrinterRegistry(IOptions<BambuOptions> options)
    {
        _options = options.Value;

        var index = 0;
        foreach (var entry in _options.Printers)
        {
            index++;
            var alias = string.IsNullOrWhiteSpace(entry.Alias) ? $"bambu-{index}" : entry.Alias.Trim();
            while (_byAlias.ContainsKey(alias)) alias = $"bambu-{++index}";
            entry.Alias = alias;
            _byAlias[alias] = entry;
        }

        var preferred = _options.DefaultAlias;
        _defaultAlias = !string.IsNullOrWhiteSpace(preferred) && _byAlias.ContainsKey(preferred)
            ? _byAlias.Keys.First(k => string.Equals(k, preferred, StringComparison.OrdinalIgnoreCase))
            : _byAlias.Keys.FirstOrDefault();
    }

    public IReadOnlyDictionary<string, BambuPrinterEntry> Printers => _byAlias;
    public string? DefaultAlias => _defaultAlias;
    public bool HasPrinters => _byAlias.Count > 0;

    public BambuPrinterEntry ResolveAlias(string? alias)
    {
        if (_byAlias.Count == 0)
        {
            throw new McpException(
                "No printers are configured. Add at least one entry under Bambu:Printers with " +
                "Host, SerialNumber, and AccessCode (all shown on the printer's network screen in LAN mode).");
        }

        if (!string.IsNullOrWhiteSpace(alias))
        {
            if (_byAlias.TryGetValue(alias, out var direct)) return direct;
            throw new McpException(
                $"Unknown printer alias '{alias}'. Available: {string.Join(", ", _byAlias.Keys)}.");
        }

        return _byAlias[_defaultAlias!];
    }

    /// <summary>Get (creating on first use) the connection for an alias.</summary>
    public PrinterConnection Get(string? alias)
    {
        var entry = ResolveAlias(alias);
        var lazy = _connections.GetOrAdd(
            entry.Alias,
            _ => new Lazy<PrinterConnection>(() => new PrinterConnection(entry, _options), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lazy in _connections.Values)
        {
            if (lazy.IsValueCreated)
            {
                await lazy.Value.DisposeAsync();
            }
        }
    }
}
