using System.Net.WebSockets;
using System.Text;
using JALib.Tools.ByteTool;

namespace JALib.Tools;

public class JAWebSocketClient : IDisposable
{
    private readonly bool _autoConnect;
    private readonly CancellationTokenSource _lifetime = new();
    private ClientWebSocket _socket = new();
    private JAction? _read;
    private JAction? _onClose;
    private JAction? _onConnect;
    private Task? _readPump;
    private bool _disposed;

    public JAWebSocketClient(JAction? read = null, bool autoConnect = true)
    {
        _read = read;
        _autoConnect = autoConnect;
    }

    public JAWebSocketClient(Uri uri, JAction? read = null, bool autoConnect = true)
        : this(read, autoConnect)
        => Connect(uri);

    public JAWebSocketClient(string uri, JAction? read = null, bool autoConnect = true)
        : this(read, autoConnect)
        => Connect(uri);

    public bool Connected => _socket.State == WebSocketState.Open;

    public void Connect(string uri, CancellationToken token = default)
        => Connect(new Uri(uri), token);
    public void Connect(Uri uri, CancellationToken token = default)
        => ConnectAsync(uri, token).GetAwaiter().GetResult();
    public Task ConnectAsync(string uri, CancellationToken token = default)
        => ConnectAsync(new Uri(uri), token);

    public async Task ConnectAsync(Uri uri, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        while (true)
        {
            try
            {
                if (_socket.State != WebSocketState.None)
                {
                    _socket.Dispose();
                    _socket = new ClientWebSocket();
                }
                await _socket.ConnectAsync(uri, linked.Token).ConfigureAwait(false);
                _onConnect?.Invoke();
                StartReadPump();
                return;
            }
            catch when (_autoConnect && !linked.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), linked.Token).ConfigureAwait(false);
            }
        }
    }

    public void SetConnectAction(JAction action) => _onConnect = action;
    public void SetCloseAction(JAction action)
    {
        _onClose = action;
        StartReadPump();
    }

    public byte ReadByte() => ReadBytes(1)[0];
    public short ReadShort() => ReadBytes(2).ToShort();
    public int ReadInt() => ReadBytes(4).ToInt();
    public long ReadLong() => ReadBytes(8).ToLong();
    public float ReadFloat() => ReadBytes(4).ToFloat();
    public double ReadDouble() => ReadBytes(8).ToDouble();
    public bool ReadBoolean() => ReadByte() != 0;
    public byte[] ReadBytesAndCount() => ReadBytes(ReadInt());
    public string ReadUTF() => Encoding.UTF8.GetString(ReadBytesAndCount());

    public byte[] ReadBytes(int count, bool force = true)
        => ReadAsyncBytes(count, force).GetAwaiter().GetResult();

    public byte[] ReadBytes()
    {
        using var stream = ReadStream();
        return stream.ToArray();
    }

    public MemoryStream ReadStream()
        => ReadStreamAsync().GetAwaiter().GetResult();

    public async Task<byte[]> ReadAsyncBytes(int count, bool force = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        RequireConnected();
        var buffer = new byte[count];
        if (count == 0)
            return buffer;
        var offset = 0;
        do
        {
            var result = await _socket.ReceiveAsync(
                    buffer.AsMemory(offset, count - offset),
                    _lifetime.Token)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new EndOfStreamException("WebSocket closed while reading.");
            offset += result.Count;
            if (!force || result.EndOfMessage)
                break;
        } while (offset < count);
        if (offset != count)
            throw new InvalidOperationException("Failed to read bytes.");
        return buffer;
    }

    public async Task<byte> ReadAsyncByte() => (await ReadAsyncBytes(1).ConfigureAwait(false))[0];
    public async Task<short> ReadAsyncShort() => (await ReadAsyncBytes(2).ConfigureAwait(false)).ToShort();
    public async Task<int> ReadAsyncInt() => (await ReadAsyncBytes(4).ConfigureAwait(false)).ToInt();
    public async Task<long> ReadAsyncLong() => (await ReadAsyncBytes(8).ConfigureAwait(false)).ToLong();
    public async Task<float> ReadAsyncFloat() => (await ReadAsyncBytes(4).ConfigureAwait(false)).ToFloat();
    public async Task<double> ReadAsyncDouble() => (await ReadAsyncBytes(8).ConfigureAwait(false)).ToDouble();
    public async Task<bool> ReadAsyncBoolean() => (await ReadAsyncBytes(1).ConfigureAwait(false))[0] != 0;
    public async Task<byte[]> ReadAsyncBytesAndCount()
        => await ReadAsyncBytes(await ReadAsyncInt().ConfigureAwait(false)).ConfigureAwait(false);
    public async Task<string> ReadAsyncUTF()
        => Encoding.UTF8.GetString(await ReadAsyncBytesAndCount().ConfigureAwait(false));

    public void WriteBytes(byte[] data, bool endOfMessage = true)
        => WriteAsyncBytes(data, endOfMessage).GetAwaiter().GetResult();
    public void WriteBytesAndCount(byte[] data, bool endOfMessage = true)
        => WriteBytes(data.Length.ToBytes().Concat(data).ToArray(), endOfMessage);
    public void WriteByte(byte value, bool endOfMessage = true)
        => WriteBytes([value], endOfMessage);
    public void WriteShort(short value, bool endOfMessage = true)
        => WriteBytes(value.ToBytes(), endOfMessage);
    public void WriteInt(int value, bool endOfMessage = true)
        => WriteBytes(value.ToBytes(), endOfMessage);
    public void WriteLong(long value, bool endOfMessage = true)
        => WriteBytes(value.ToBytes(), endOfMessage);
    public void WriteFloat(float value, bool endOfMessage = true)
        => WriteBytes(value.ToBytes(), endOfMessage);
    public void WriteDouble(double value, bool endOfMessage = true)
        => WriteBytes(value.ToBytes(), endOfMessage);
    public void WriteBoolean(bool value, bool endOfMessage = true)
        => WriteByte((byte)(value ? 1 : 0), endOfMessage);
    public void WriteUTF(string value, bool endOfMessage = true)
        => WriteBytesAndCount(Encoding.UTF8.GetBytes(value), endOfMessage);

    public Task WriteAsyncBytes(byte[] data, bool endOfMessage = true)
    {
        RequireConnected();
        return _socket.SendAsync(
            data,
            WebSocketMessageType.Binary,
            endOfMessage,
            _lifetime.Token);
    }
    public Task WriteAsyncBytesAndCount(byte[] data, bool endOfMessage = true)
        => WriteAsyncBytes(data.Length.ToBytes().Concat(data).ToArray(), endOfMessage);
    public Task WriteAsyncByte(byte value, bool endOfMessage = true)
        => WriteAsyncBytes([value], endOfMessage);
    public Task WriteAsyncShort(short value, bool endOfMessage = true)
        => WriteAsyncBytes(value.ToBytes(), endOfMessage);
    public Task WriteAsyncInt(int value, bool endOfMessage = true)
        => WriteAsyncBytes(value.ToBytes(), endOfMessage);
    public Task WriteAsyncLong(long value, bool endOfMessage = true)
        => WriteAsyncBytes(value.ToBytes(), endOfMessage);
    public Task WriteAsyncFloat(float value, bool endOfMessage = true)
        => WriteAsyncBytes(value.ToBytes(), endOfMessage);
    public Task WriteAsyncDouble(double value, bool endOfMessage = true)
        => WriteAsyncBytes(value.ToBytes(), endOfMessage);
    public Task WriteAsyncBoolean(bool value, bool endOfMessage = true)
        => WriteAsyncByte((byte)(value ? 1 : 0), endOfMessage);
    public Task WriteAsyncUTF(string value, bool endOfMessage = true)
        => WriteAsyncBytesAndCount(Encoding.UTF8.GetBytes(value), endOfMessage);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _socket.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<MemoryStream> ReadStreamAsync()
    {
        RequireConnected();
        var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, _lifetime.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new EndOfStreamException("WebSocket closed while reading.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        stream.Position = 0;
        return stream;
    }

    private void RequireConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Connected)
            throw new InvalidOperationException(nameof(WebSocket) + " is not connected");
    }

    private void StartReadPump()
    {
        if (!Connected || (_read == null && _onClose == null) ||
            _readPump is { IsCompleted: false })
        {
            return;
        }
        _readPump = Task.Run(async () =>
        {
            try
            {
                while (Connected && !_lifetime.IsCancellationRequested)
                {
                    if (_read != null)
                        _read.Invoke();
                    else
                        await Task.Delay(100, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                if (!_lifetime.IsCancellationRequested)
                    _onClose?.Invoke();
            }
        });
    }
}
