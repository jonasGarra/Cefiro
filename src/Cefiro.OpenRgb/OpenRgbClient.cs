using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace Cefiro.OpenRgb;

/// <summary>Datos de un aviso SIGNALUPDATE del servidor (protocolo 6+).</summary>
public sealed class DeviceSignalEventArgs(uint deviceId, UpdateReason reason) : EventArgs
{
    public uint DeviceId { get; } = deviceId;
    public UpdateReason Reason { get; } = reason;
}

/// <summary>ACK con error (protocolo 6+).</summary>
public sealed class AckErrorEventArgs(uint deviceId, PacketId ackedPacket, PacketStatus status) : EventArgs
{
    public uint DeviceId { get; } = deviceId;
    public PacketId AckedPacket { get; } = ackedPacket;
    public PacketStatus Status { get; } = status;
}

/// <summary>
/// Cliente del SDK de red de OpenRGB. Negocia la versión de protocolo con el servidor
/// (se usa la menor de las dos) y soporta desde la 0 hasta la 6 (OpenRGB 1.x).
/// Todas las operaciones son seguras entre hilos.
/// </summary>
public sealed class OpenRgbClient : IAsyncDisposable
{
    private static readonly TimeSpan ProtocolVersionTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _host;
    private readonly int _port;
    private readonly string _clientName;
    private readonly uint _maxProtocolVersion;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, int> _outstandingLedUpdates = new();
    private readonly object _pendingGate = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private PendingRequest? _pending;
    private volatile bool _connected;
    private int _disposed;

    public OpenRgbClient(
        string clientName = "Céfiro",
        string host = OpenRgbProtocol.DefaultHost,
        int port = OpenRgbProtocol.DefaultPort,
        uint maxProtocolVersion = OpenRgbProtocol.MaxSupportedVersion)
    {
        _clientName = clientName;
        _host = host;
        _port = port;
        _maxProtocolVersion = Math.Min(maxProtocolVersion, OpenRgbProtocol.MaxSupportedVersion);
    }

    /// <summary>Versión de protocolo negociada (la menor entre cliente y servidor).</summary>
    public uint ProtocolVersion { get; private set; }

    /// <summary>Versión máxima que anuncia el servidor.</summary>
    public uint ServerProtocolVersion { get; private set; }

    public string? ServerName { get; private set; }
    public ServerFlags ServerFlags { get; private set; }
    public bool IsConnected => _connected;

    /// <summary>El servidor ha cambiado su lista de dispositivos: hay que volver a pedirla.</summary>
    public event EventHandler? DeviceListUpdated;

    public event EventHandler? DetectionStarted;
    public event EventHandler? DetectionCompleted;
    public event EventHandler<DeviceSignalEventArgs>? DeviceSignal;
    public event EventHandler<AckErrorEventArgs>? AckError;

    /// <summary>Se ha perdido la conexión (la excepción puede ser null si se cerró de forma ordenada).</summary>
    public event EventHandler<Exception?>? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_connected)
            throw new InvalidOperationException("El cliente ya está conectado.");

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
        _readCts = new CancellationTokenSource();
        _connected = true;
        _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token));

        // 1) Negociar la versión. Un servidor de protocolo 0 no responde: se asume 0.
        var reply = await RequestAsync(
            PacketId.RequestProtocolVersion, 0, Payloads.UInt32(_maxProtocolVersion),
            expectedDeviceId: null, ProtocolVersionTimeout, allowTimeout: true, cancellationToken).ConfigureAwait(false);

        ServerProtocolVersion = reply is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(reply) : 0;
        ProtocolVersion = Math.Min(ServerProtocolVersion, _maxProtocolVersion);

        // 2) Presentarse.
        await SendAsync(PacketId.SetClientName, 0, Payloads.NullTerminated(_clientName), cancellationToken).ConfigureAwait(false);

        if (ProtocolVersion >= 6)
        {
            await SendAsync(PacketId.SetClientFlags, 0,
                Payloads.UInt32((uint)ClientFlags.SupportsRgbController), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// IDs de los controladores: IDs únicos (protocolo 6+) o índices 0..n-1 (protocolos 0-5).
    /// </summary>
    public async Task<IReadOnlyList<uint>> RequestControllerIdsAsync(CancellationToken cancellationToken = default)
    {
        var data = await RequestAsync(PacketId.RequestControllerCount, 0, [], null,
            DefaultRequestTimeout, allowTimeout: false, cancellationToken).ConfigureAwait(false);

        if (data is null || data.Length < 4)
            throw new OpenRgbProtocolException("Respuesta de número de controladores vacía.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var ids = new uint[count];

        if (ProtocolVersion >= 6)
        {
            if (data.Length < 4 + count * 4)
                throw new OpenRgbProtocolException("La lista de IDs de controladores está incompleta.");

            for (var i = 0; i < count; i++)
                ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + i * 4));
        }
        else
        {
            for (uint i = 0; i < count; i++)
                ids[i] = i;
        }

        return ids;
    }

    public async Task<OpenRgbDevice> RequestControllerAsync(uint deviceId, CancellationToken cancellationToken = default)
    {
        var protocol = ProtocolVersion;
        var payload = protocol >= 1 ? Payloads.UInt32(protocol) : [];

        var data = await RequestAsync(PacketId.RequestControllerData, deviceId, payload, deviceId,
            DefaultRequestTimeout, allowTimeout: false, cancellationToken).ConfigureAwait(false);

        if (data is null)
            throw new OpenRgbProtocolException($"Sin datos del controlador {deviceId}.");

        return DeviceDataParser.Parse(data, protocol, deviceId);
    }

    public async Task<IReadOnlyList<OpenRgbDevice>> RequestAllControllersAsync(CancellationToken cancellationToken = default)
    {
        var ids = await RequestControllerIdsAsync(cancellationToken).ConfigureAwait(false);
        var devices = new List<OpenRgbDevice>(ids.Count);
        foreach (var id in ids)
            devices.Add(await RequestControllerAsync(id, cancellationToken).ConfigureAwait(false));
        return devices;
    }

    /// <summary>Envía el color de todos los LEDs del dispositivo (debe incluir todos los LEDs).</summary>
    public ValueTask UpdateLedsAsync(uint deviceId, ReadOnlySpan<OpenRgbColor> colors, CancellationToken cancellationToken = default)
    {
        var payload = Payloads.UpdateLeds(colors);
        TrackLedUpdate(deviceId);
        return SendAsync(PacketId.UpdateLeds, deviceId, payload, cancellationToken);
    }

    /// <summary>Envía el color de todos los LEDs de una zona.</summary>
    public ValueTask UpdateZoneLedsAsync(uint deviceId, int zoneIndex, ReadOnlySpan<OpenRgbColor> colors, CancellationToken cancellationToken = default)
    {
        var payload = Payloads.UpdateZoneLeds(zoneIndex, colors);
        TrackLedUpdate(deviceId);
        return SendAsync(PacketId.UpdateZoneLeds, deviceId, payload, cancellationToken);
    }

    public ValueTask UpdateSingleLedAsync(uint deviceId, int ledIndex, OpenRgbColor color, CancellationToken cancellationToken = default) =>
        SendAsync(PacketId.UpdateSingleLed, deviceId, Payloads.UpdateSingleLed(ledIndex, color), cancellationToken);

    /// <summary>Pone el dispositivo en su modo de control directo ("Direct"/"Custom").</summary>
    public ValueTask SetCustomModeAsync(uint deviceId, CancellationToken cancellationToken = default) =>
        SendAsync(PacketId.SetCustomMode, deviceId, [], cancellationToken);

    /// <summary>Activa y configura un modo del dispositivo (no lo guarda en su memoria).</summary>
    public ValueTask UpdateModeAsync(uint deviceId, OpenRgbMode mode, CancellationToken cancellationToken = default) =>
        SendAsync(PacketId.UpdateMode, deviceId, Payloads.UpdateMode(mode.Index, mode, ProtocolVersion), cancellationToken);

    /// <summary>Activa un modo y lo guarda en la memoria del dispositivo (si lo admite).</summary>
    public ValueTask SaveModeAsync(uint deviceId, OpenRgbMode mode, CancellationToken cancellationToken = default)
    {
        if (ProtocolVersion < 3)
            throw new NotSupportedException("SaveMode necesita protocolo 3 o superior.");
        return SendAsync(PacketId.SaveMode, deviceId, Payloads.UpdateMode(mode.Index, mode, ProtocolVersion), cancellationToken);
    }

    /// <summary>Modo por zona (protocolo 6+).</summary>
    public ValueTask UpdateZoneModeAsync(uint deviceId, int zoneIndex, OpenRgbMode mode, CancellationToken cancellationToken = default)
    {
        if (ProtocolVersion < 6)
            throw new NotSupportedException("Los modos por zona necesitan protocolo 6 (OpenRGB 1.0+).");
        return SendAsync(PacketId.UpdateZoneMode, deviceId,
            Payloads.UpdateZoneMode(zoneIndex, mode.Index, mode, ProtocolVersion), cancellationToken);
    }

    /// <summary>Cambia el número de LEDs de una zona redimensionable (cabezales ARGB).</summary>
    public ValueTask ResizeZoneAsync(uint deviceId, int zoneIndex, int newSize, CancellationToken cancellationToken = default) =>
        SendAsync(PacketId.ResizeZone, deviceId, Payloads.ResizeZone(zoneIndex, newSize), cancellationToken);

    /// <summary>Pide al servidor que vuelva a detectar dispositivos (protocolo 5+).</summary>
    public ValueTask RescanDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (ProtocolVersion < 5)
            throw new NotSupportedException("La redetección necesita protocolo 5 o superior.");
        return SendAsync(PacketId.RequestRescanDevices, 0, [], cancellationToken);
    }

    /// <summary>
    /// Actualizaciones de LEDs enviadas al dispositivo cuyo ACK aún no ha llegado (solo protocolo 6+).
    /// Sirve para no saturar dispositivos lentos (p. ej. la RAM por SMBus).
    /// </summary>
    public int GetOutstandingLedUpdates(uint deviceId) =>
        _outstandingLedUpdates.TryGetValue(deviceId, out var n) ? n : 0;

    private void TrackLedUpdate(uint deviceId)
    {
        if (ProtocolVersion >= 6)
            _outstandingLedUpdates.AddOrUpdate(deviceId, 1, static (_, n) => n + 1);
    }

    private async Task<byte[]?> RequestAsync(
        PacketId id, uint deviceId, byte[] payload, uint? expectedDeviceId,
        TimeSpan timeout, bool allowTimeout, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new PendingRequest(id, expectedDeviceId);
            lock (_pendingGate)
                _pending = pending;

            try
            {
                await SendAsync(id, deviceId, payload, cancellationToken).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                try
                {
                    return await pending.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (allowTimeout)
                        return null;
                    throw new TimeoutException($"El servidor de OpenRGB no respondió a {id} en {timeout.TotalSeconds:0} s.");
                }
            }
            finally
            {
                lock (_pendingGate)
                {
                    if (ReferenceEquals(_pending, pending))
                        _pending = null;
                }
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async ValueTask SendAsync(PacketId id, uint deviceId, byte[] payload, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (!_connected || stream is null)
            throw new InvalidOperationException("No hay conexión con el servidor de OpenRGB.");

        var total = OpenRgbProtocol.HeaderSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            new PacketHeader(deviceId, id, (uint)payload.Length).WriteTo(buffer);
            payload.CopyTo(buffer, OpenRgbProtocol.HeaderSize);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(buffer.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            HandleDisconnect(ex);
            throw new InvalidOperationException("Se perdió la conexión con el servidor de OpenRGB.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = new byte[OpenRgbProtocol.HeaderSize];
        Exception? failure = null;

        try
        {
            var stream = _stream!;
            while (!cancellationToken.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(headerBuffer, cancellationToken).ConfigureAwait(false);
                var header = PacketHeader.Read(headerBuffer);

                if (header.Size > OpenRgbProtocol.MaxPacketSize)
                    throw new OpenRgbProtocolException($"Paquete demasiado grande ({header.Size} bytes).");

                var data = header.Size == 0 ? [] : new byte[header.Size];
                if (data.Length > 0)
                    await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);

                Dispatch(header, data);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cierre ordenado.
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        HandleDisconnect(failure);
    }

    private void Dispatch(PacketHeader header, byte[] data)
    {
        switch (header.Id)
        {
            case PacketId.Ack:
                if (data.Length >= 8)
                {
                    var acked = (PacketId)BinaryPrimitives.ReadUInt32LittleEndian(data);
                    var status = (PacketStatus)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));

                    if (acked is PacketId.UpdateLeds or PacketId.UpdateZoneLeds)
                    {
                        _outstandingLedUpdates.AddOrUpdate(header.DeviceId, 0, static (_, n) => Math.Max(0, n - 1));
                    }

                    if (status != PacketStatus.Ok)
                        Raise(AckError, new AckErrorEventArgs(header.DeviceId, acked, status));
                }
                return;

            case PacketId.SetServerName:
                ServerName = DecodeNullTerminated(data);
                return;

            case PacketId.SetServerFlags:
                if (data.Length >= 4)
                    ServerFlags = (ServerFlags)BinaryPrimitives.ReadUInt32LittleEndian(data);
                return;

            case PacketId.SetServerHostname:
            case PacketId.DetectionProgressChanged:
            case PacketId.LogManagerLoggedEntry:
                return;

            case PacketId.DeviceListUpdated:
                _outstandingLedUpdates.Clear();
                Raise(DeviceListUpdated, EventArgs.Empty);
                return;

            case PacketId.DetectionStarted:
                Raise(DetectionStarted, EventArgs.Empty);
                return;

            case PacketId.DetectionComplete:
                Raise(DetectionCompleted, EventArgs.Empty);
                return;

            case PacketId.SignalUpdate:
                if (data.Length >= 8)
                {
                    var reason = (UpdateReason)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                    Raise(DeviceSignal, new DeviceSignalEventArgs(header.DeviceId, reason));
                }
                return;
        }

        PendingRequest? pending;
        lock (_pendingGate)
            pending = _pending;

        if (pending is not null
            && pending.ExpectedId == header.Id
            && (pending.ExpectedDeviceId is null || pending.ExpectedDeviceId == header.DeviceId))
        {
            pending.Completion.TrySetResult(data);
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
            return;

        // Los manejadores se ejecutan fuera del hilo de lectura para que puedan volver a llamar al cliente.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(this, args); }
            catch { /* Un manejador que falla no debe tirar el cliente. */ }
        });
    }

    private void Raise(EventHandler? handler, EventArgs args)
    {
        if (handler is null)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(this, args); }
            catch { /* Un manejador que falla no debe tirar el cliente. */ }
        });
    }

    private void HandleDisconnect(Exception? reason)
    {
        if (!_connected)
            return;

        _connected = false;

        PendingRequest? pending;
        lock (_pendingGate)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.Completion.TrySetException(
            new InvalidOperationException("Se perdió la conexión con el servidor de OpenRGB.", reason));

        try { _tcp?.Dispose(); } catch { /* ya cerrado */ }

        // Al liberar el cliente a propósito no se avisa de una desconexión.
        if (Volatile.Read(ref _disposed) == 0)
            Raise(Disconnected, reason);
    }

    private static string DecodeNullTerminated(byte[] data)
    {
        var end = Array.IndexOf(data, (byte)0);
        return Encoding.UTF8.GetString(data, 0, end < 0 ? data.Length : end);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _readCts?.Cancel();
        try { _tcp?.Dispose(); } catch { /* ya cerrado */ }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); }
            catch { /* el bucle ya informa del error */ }
        }

        _connected = false;
        _readCts?.Dispose();
        _sendLock.Dispose();
        _requestLock.Dispose();
    }

    private sealed class PendingRequest(PacketId expectedId, uint? expectedDeviceId)
    {
        public PacketId ExpectedId { get; } = expectedId;
        public uint? ExpectedDeviceId { get; } = expectedDeviceId;
        public TaskCompletionSource<byte[]> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
