using System.Net;
using CoreRCON;

namespace ZomboidManager;

/// <summary>
/// Shared RCON client: reuses one TCP connection across calls, closes after idle,
/// and transparently reconnects once on failure.
/// </summary>
public sealed class RconManager : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _idleGate = new();
    private RCON? _rcon;
    private string? _connectionKey;
    private DateTime _lastUsedUtc = DateTime.MinValue;
    private CancellationTokenSource? _idleCts;
    private bool _disposed;

    public async Task<string> SendCommandAsync(string host, int port, string password, string command)
    {
        if (_disposed)
            return "RCON error: Manager disposed.";

        if (!IPAddress.TryParse(host, out IPAddress? ipAddress))
            return $"RCON error: Invalid host address '{host}'.";

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SendWithReuseAsync(ipAddress, port, password ?? string.Empty, command, allowRetry: true)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _gate.Wait(2000);
        }
        catch
        {
            // ignore
        }

        try
        {
            InvalidateConnection();
        }
        finally
        {
            try { _gate.Release(); } catch { /* ignore */ }
            _gate.Dispose();
        }
    }

    private async Task<string> SendWithReuseAsync(
        IPAddress ipAddress,
        int port,
        string password,
        string command,
        bool allowRetry)
    {
        string key = BuildKey(ipAddress, port, password);
        try
        {
            await EnsureConnectedAsync(ipAddress, port, password, key).ConfigureAwait(false);
            string response = await _rcon!.SendCommandAsync(command).ConfigureAwait(false);
            NoteUsed();
            return response;
        }
        catch (Exception ex)
        {
            InvalidateConnection();
            if (!allowRetry)
                return $"RCON error: {ex.Message}";

            try
            {
                await EnsureConnectedAsync(ipAddress, port, password, key).ConfigureAwait(false);
                string response = await _rcon!.SendCommandAsync(command).ConfigureAwait(false);
                NoteUsed();
                return response;
            }
            catch (Exception retryEx)
            {
                InvalidateConnection();
                return $"RCON error: {retryEx.Message}";
            }
        }
    }

    private async Task EnsureConnectedAsync(IPAddress ipAddress, int port, string password, string key)
    {
        if (_rcon is not null
            && string.Equals(_connectionKey, key, StringComparison.Ordinal)
            && DateTime.UtcNow - _lastUsedUtc <= IdleTimeout
            && _rcon.Connected
            && _rcon.Authenticated)
        {
            return;
        }

        // Endpoint changed, idle expired, or socket dropped — open a fresh client.
        InvalidateConnection();

        var client = new RCON(ipAddress, (ushort)port, password, autoConnect: true);
        await client.ConnectAsync().ConfigureAwait(false);
        _rcon = client;
        _connectionKey = key;
        NoteUsed();
    }

    private void NoteUsed()
    {
        _lastUsedUtc = DateTime.UtcNow;
        ScheduleIdleClose();
    }

    private void ScheduleIdleClose()
    {
        CancellationTokenSource cts;
        lock (_idleGate)
        {
            _idleCts?.Cancel();
            _idleCts?.Dispose();
            _idleCts = new CancellationTokenSource();
            cts = _idleCts;
        }

        _ = CloseWhenIdleAsync(cts);
    }

    private async Task CloseWhenIdleAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(IdleTimeout, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || cts.IsCancellationRequested)
            return;

        // Only close if still idle (no newer command reset the timer).
        if (DateTime.UtcNow - _lastUsedUtc < IdleTimeout - TimeSpan.FromMilliseconds(250))
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            if (DateTime.UtcNow - _lastUsedUtc < IdleTimeout - TimeSpan.FromMilliseconds(250))
                return;
            InvalidateConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InvalidateConnection()
    {
        lock (_idleGate)
        {
            try { _idleCts?.Cancel(); } catch { /* ignore */ }
            try { _idleCts?.Dispose(); } catch { /* ignore */ }
            _idleCts = null;
        }

        RCON? client = _rcon;
        _rcon = null;
        _connectionKey = null;
        if (client is null)
            return;

        try
        {
            client.Dispose();
        }
        catch
        {
            // ignore dispose races with a dead socket
        }
    }

    private static string BuildKey(IPAddress ip, int port, string password) =>
        $"{ip}:{port}:{password}";
}
