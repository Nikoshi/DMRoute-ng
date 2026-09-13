using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace DMRoute_ng.Emulator;

public sealed class VirtualHotspot : IAsyncDisposable
{
    private const int ReceiveBufferSize = 4096;

    private readonly VirtualHotspotOptions _options;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<HomebrewDatagram> _controlPackets;
    private readonly Channel<HomebrewDatagram> _dmrdPackets;
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Task _receiverTask;
    private CancellationTokenSource? _keepaliveCancellation;
    private Task? _keepaliveTask;
    private int _state;
    private int _disposed;

    public VirtualHotspot(VirtualHotspotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.MasterEndPoint);
        ArgumentNullException.ThrowIfNull(options.Configuration);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        ArgumentException.ThrowIfNullOrEmpty(options.PreSharedKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RepeaterId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ReceiveQueueCapacity);
        if (options.MasterEndPoint.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 master endpoints are supported.", nameof(options));
        if (options.OperationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "OperationTimeout must be positive.");
        if (options.KeepaliveInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "KeepaliveInterval must be positive when configured.");

        _options = options;
        _controlPackets = CreateChannel(Math.Min(options.ReceiveQueueCapacity, 32));
        _dmrdPackets = CreateChannel(options.ReceiveQueueCapacity);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _socket.Connect(options.MasterEndPoint);
        _receiverTask = ReceiveLoopAsync(_lifetime.Token);
    }

    public int RepeaterId => _options.RepeaterId;
    public TimeProvider TimeProvider => _options.TimeProvider;
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    public VirtualHotspotState State => (VirtualHotspotState)Volatile.Read(ref _state);

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not (VirtualHotspotState.Disconnected or VirtualHotspotState.Rejected or VirtualHotspotState.Faulted))
                throw new InvalidOperationException($"Cannot log in while the hotspot is {State}.");

            SetState(VirtualHotspotState.Authenticating);
            Drain(_controlPackets.Reader);

            await SendPacketAsync(HomebrewProtocol.CreateRptl(RepeaterId), cancellationToken).ConfigureAwait(false);
            var challenge = await ReadControlAsync("RPTACK challenge", cancellationToken).ConfigureAwait(false);
            if (!HomebrewProtocol.TryReadRptAck(challenge.Payload, out var salt))
                throw UnexpectedPacket("RPTACK challenge", challenge.Payload);

            await SendPacketAsync(
                HomebrewProtocol.CreateRptk(RepeaterId, salt, _options.PreSharedKey), cancellationToken).ConfigureAwait(false);
            await ExpectRptAckAsync("RPTACK authentication", cancellationToken).ConfigureAwait(false);

            await SendPacketAsync(
                HomebrewProtocol.CreateRptc(RepeaterId, _options.Configuration), cancellationToken).ConfigureAwait(false);
            await ExpectRptAckAsync("RPTACK configuration", cancellationToken).ConfigureAwait(false);

            SetState(VirtualHotspotState.Configured);
            StartKeepaliveIfConfigured();
        }
        catch (HomebrewRejectedException)
        {
            SetState(VirtualHotspotState.Rejected);
            throw;
        }
        catch (OperationCanceledException)
        {
            SetState(VirtualHotspotState.Disconnected);
            throw;
        }
        catch
        {
            SetState(VirtualHotspotState.Faulted);
            throw;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != VirtualHotspotState.Configured)
                throw new InvalidOperationException($"Cannot ping while the hotspot is {State}.");

            await SendPacketAsync(HomebrewProtocol.CreateRptPing(RepeaterId), cancellationToken).ConfigureAwait(false);
            var response = await ReadControlAsync("MSTPONG", cancellationToken).ConfigureAwait(false);
            if (!HomebrewProtocol.TryReadMstPong(response.Payload, out var repeaterId) || repeaterId != RepeaterId)
                throw UnexpectedPacket("MSTPONG", response.Payload);
        }
        catch (HomebrewRejectedException)
        {
            SetState(VirtualHotspotState.Rejected);
            throw;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task SendDmrdAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State != VirtualHotspotState.Configured)
            throw new InvalidOperationException($"Cannot send DMRD while the hotspot is {State}.");
        if (!HomebrewProtocol.IsDmrd(packet.Span))
            throw new ArgumentException("The packet is not a complete DMRD datagram.", nameof(packet));
        if (HomebrewProtocol.ReadDmrdRepeaterId(packet.Span) != RepeaterId)
            throw new ArgumentException("The DMRD repeater ID does not match this hotspot.", nameof(packet));

        await SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public Task<HomebrewDatagram> ReceiveDmrdAsync(CancellationToken cancellationToken = default) =>
        ReceiveDmrdAsync(_options.OperationTimeout, cancellationToken);

    public Task<HomebrewDatagram> ReceiveDmrdAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return ReadPacketAsync(_dmrdPackets.Reader, "DMRD packet", timeout, cancellationToken);
    }

    public bool TryReceiveDmrd(out HomebrewDatagram? datagram) =>
        _dmrdPackets.Reader.TryRead(out datagram);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopKeepaliveAsync().ConfigureAwait(false);
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == VirtualHotspotState.Disconnected) return;
            await SendPacketAsync(HomebrewProtocol.CreateRptClose(RepeaterId), cancellationToken).ConfigureAwait(false);
            SetState(VirtualHotspotState.Disconnected);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await StopKeepaliveAsync().ConfigureAwait(false);
        _lifetime.Cancel();
        _socket.Dispose();
        try
        {
            await _receiverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _lifetime.Dispose();
        _controlGate.Dispose();
        _sendGate.Dispose();
        SetState(VirtualHotspotState.Disconnected);
    }

    private static Channel<HomebrewDatagram> CreateChannel(int capacity) =>
        Channel.CreateBounded<HomebrewDatagram>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (received == 0) continue;

                var datagram = new HomebrewDatagram(
                    buffer.AsSpan(0, received).ToArray(),
                    _options.TimeProvider.GetUtcNow());
                var target = HomebrewProtocol.IsDmrd(datagram.Payload) ? _dmrdPackets.Writer : _controlPackets.Writer;
                if (target.TryWrite(datagram)) continue;

                throw new HomebrewProtocolException(
                    $"The bounded receive queue for repeater {RepeaterId} is full.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetState(VirtualHotspotState.Faulted);
            _controlPackets.Writer.TryComplete(exception);
            _dmrdPackets.Writer.TryComplete(exception);
            return;
        }

        _controlPackets.Writer.TryComplete();
        _dmrdPackets.Writer.TryComplete();
    }

    private async Task ExpectRptAckAsync(string operation, CancellationToken cancellationToken)
    {
        var response = await ReadControlAsync(operation, cancellationToken).ConfigureAwait(false);
        if (!HomebrewProtocol.TryReadRptAck(response.Payload, out var value) || value != (uint)RepeaterId)
            throw UnexpectedPacket(operation, response.Payload);
    }

    private async Task<HomebrewDatagram> ReadControlAsync(string operation, CancellationToken cancellationToken)
    {
        var response = await ReadPacketAsync(
            _controlPackets.Reader, operation, _options.OperationTimeout, cancellationToken).ConfigureAwait(false);
        if (HomebrewProtocol.TryReadMstNak(response.Payload, out var repeaterId))
            throw new HomebrewRejectedException(repeaterId);
        return response;
    }

    private async Task<HomebrewDatagram> ReadPacketAsync(
        ChannelReader<HomebrewDatagram> reader,
        string operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout, _options.TimeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token, timeoutCancellation.Token);
        try
        {
            return await reader.ReadAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested &&
            !_lifetime.IsCancellationRequested)
        {
            throw new HomebrewTimeoutException(operation, timeout);
        }
    }

    private async Task SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sent = await _socket.SendAsync(packet, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (sent != packet.Length)
                throw new HomebrewProtocolException($"UDP send wrote {sent} of {packet.Length} bytes.");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void StartKeepaliveIfConfigured()
    {
        if (_options.KeepaliveInterval is not { } interval) return;

        _keepaliveCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _keepaliveTask = KeepaliveLoopAsync(interval, _keepaliveCancellation.Token);
    }

    private async Task KeepaliveLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval, _options.TimeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            SetState(VirtualHotspotState.Faulted);
            throw;
        }
    }

    private async Task StopKeepaliveAsync()
    {
        var cancellation = Interlocked.Exchange(ref _keepaliveCancellation, null);
        var task = Interlocked.Exchange(ref _keepaliveTask, null);
        if (cancellation is null) return;

        cancellation.Cancel();
        try
        {
            if (task is not null) await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void Drain(ChannelReader<HomebrewDatagram> reader)
    {
        while (reader.TryRead(out _))
        {
        }
    }

    private static HomebrewProtocolException UnexpectedPacket(string expected, ReadOnlySpan<byte> packet) =>
        new($"Expected {expected}, received {Convert.ToHexString(packet)}.");

    private void SetState(VirtualHotspotState state) => Volatile.Write(ref _state, (int)state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
