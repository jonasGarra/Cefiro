using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;

namespace Cefiro.Ipc;

/// <summary>
/// Servidor del named pipe. Acepta varios clientes a la vez; cada uno tiene su propia cola de salida
/// para que un cliente lento no frene a los demás ni al núcleo.
/// Permisos: SYSTEM y Administradores (control total), usuarios interactivos y el usuario actual (lectura/escritura).
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private const int EventQueueCapacity = 256;

    private readonly string _pipeName;
    private readonly IRequestHandler _handler;
    private readonly Action<string>? _log;
    private readonly Action<Exception, string>? _logError;
    private readonly ConcurrentDictionary<int, ClientSession> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextClientId;
    private Task? _acceptLoop;

    public PipeServer(string pipeName, IRequestHandler handler, Action<string>? log = null, Action<Exception, string>? logError = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _log = log;
        _logError = logError;
    }

    public int ClientCount => _clients.Count;

    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>Envía un evento a todos los clientes. Si la cola de un cliente está llena, se descarta para él.</summary>
    public void Publish(IpcEvent evt)
    {
        if (_clients.IsEmpty)
            return;

        var frame = Framing.Serialize(new Envelope { Kind = EnvelopeKind.Event, Name = evt.Name, Payload = evt.Payload });
        foreach (var client in _clients.Values)
            client.TryQueueEvent(frame);
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is { } current)
        {
            security.AddAccessRule(new PipeAccessRule(current, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var security = CreateSecurity();
        var first = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // FirstPipeInstance en la primera instancia: si otro proceso ya ha creado un pipe con
                // este nombre (para hacerse pasar por Céfiro), falla en vez de compartirlo.
                var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, options, 64 * 1024, 64 * 1024, security);
                first = false;

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var id = Interlocked.Increment(ref _nextClientId);
                var session = new ClientSession(id, pipe, this);
                _clients[id] = session;
                _log?.Invoke($"Cliente IPC {id} conectado ({_clients.Count} en total).");
                session.Start(cancellationToken);
                pipe = null; // ahora es de la sesión
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logError?.Invoke(ex, "Error aceptando clientes del pipe");
                try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private void Remove(ClientSession session)
    {
        if (_clients.TryRemove(session.Id, out _))
            _log?.Invoke($"Cliente IPC {session.Id} desconectado ({_clients.Count} en total).");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var client in _clients.Values)
            client.Close();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* ya registrado */ }
        }

        _cts.Dispose();
    }

    private sealed class ClientSession(int id, NamedPipeServerStream pipe, PipeServer server)
    {
        // Una sola cola de salida. Las respuestas siempre entran; los eventos se descartan si el
        // cliente acumula demasiado retraso (p. ej. la ventana está congelada).
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        private readonly CancellationTokenSource _cts = new();
        private int _queued;

        public int Id { get; } = id;

        public void TryQueueEvent(byte[] frame)
        {
            if (Volatile.Read(ref _queued) >= EventQueueCapacity)
                return;

            if (_outbound.Writer.TryWrite(frame))
                Interlocked.Increment(ref _queued);
        }

        public void Start(CancellationToken serverToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _cts.Token);
            _ = Task.Run(async () =>
            {
                var writer = Task.Run(() => WriteLoopAsync(linked.Token));
                try
                {
                    await ReadLoopAsync(linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    linked.Cancel();
                    try { await writer.ConfigureAwait(false); } catch { /* cierre */ }
                    linked.Dispose();
                    pipe.Dispose();
                    server.Remove(this);
                }
            });
        }

        public void Close()
        {
            try { _cts.Cancel(); } catch { /* ya cerrado */ }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var request = await Framing.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                    if (request is null)
                        break;

                    if (request.Kind != EnvelopeKind.Request || string.IsNullOrEmpty(request.Name))
                        continue;

                    // Cada petición se atiende sin bloquear la lectura de las siguientes.
                    _ = HandleAsync(request, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
            {
            }
            catch (Exception ex)
            {
                server._logError?.Invoke(ex, $"Error leyendo del cliente IPC {Id}");
            }
        }

        private async Task HandleAsync(Envelope request, CancellationToken cancellationToken)
        {
            var response = new Envelope { Kind = EnvelopeKind.Response, Id = request.Id, Name = request.Name };
            try
            {
                response.Payload = await server._handler.HandleAsync(request.Name!, request.Payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response.Error = ex.Message;
                if (ex is not (ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException))
                    server._logError?.Invoke(ex, $"Error en el método {request.Name}");
            }

            if (_outbound.Writer.TryWrite(Framing.Serialize(response)))
                Interlocked.Increment(ref _queued);
        }

        private async Task WriteLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    Interlocked.Decrement(ref _queued);
                    await Framing.WriteRawAsync(pipe, frame, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }
}
