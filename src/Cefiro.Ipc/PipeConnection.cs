using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace Cefiro.Ipc;

/// <summary>
/// Cliente del named pipe con reconexión automática: si el servicio se reinicia,
/// la interfaz vuelve a conectarse sola.
/// </summary>
public sealed class PipeConnection : ICefiroConnection
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly string _pipeName;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<Envelope>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private Task? _loop;
    private long _nextId;
    private volatile bool _connected;

    public PipeConnection(string pipeName = Contracts.IpcConstants.PipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _connected;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<IpcEvent>? EventReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _loop ??= Task.Run(() => ConnectLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Espera a que haya conexión (o vence el plazo).</summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_connected && DateTime.UtcNow < deadline)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        return _connected;
    }

    public async Task<JsonElement?> InvokeAsync(string method, JsonElement? payload, CancellationToken cancellationToken = default)
    {
        var pipe = _pipe;
        if (!_connected || pipe is null)
            throw new InvalidOperationException("No hay conexión con el servicio de Céfiro.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Framing.WriteAsync(pipe, new Envelope
                {
                    Kind = EnvelopeKind.Request,
                    Id = id,
                    Name = method,
                    Payload = payload,
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var response = await tcs.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Error is not null)
                throw new IpcRemoteException(method, response.Error);

            return response.Payload;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"El servicio no respondió a \"{method}\".");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
                _pipe = pipe;
                SetConnected(true);
                await ReadLoopAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Servicio no disponible: se reintenta.
            }
            finally
            {
                SetConnected(false);
                _pipe = null;
                FailPending();
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await Framing.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
                return;

            switch (envelope.Kind)
            {
                case EnvelopeKind.Response:
                    if (_pending.TryGetValue(envelope.Id, out var tcs))
                        tcs.TrySetResult(envelope);
                    break;

                case EnvelopeKind.Event when envelope.Name is not null:
                    try { EventReceived?.Invoke(this, new IpcEvent(envelope.Name, envelope.Payload)); }
                    catch { /* un manejador no debe cortar la conexión */ }
                    break;
            }
        }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value)
            return;

        _connected = value;
        try { ConnectionChanged?.Invoke(this, value); }
        catch { /* ignorado */ }
    }

    private void FailPending()
    {
        foreach (var (id, tcs) in _pending)
        {
            tcs.TrySetException(new InvalidOperationException("Se perdió la conexión con el servicio de Céfiro."));
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pipe is { } pipe)
            await pipe.DisposeAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* cierre */ }
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }
}
