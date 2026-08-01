using System.Net;
using System.Net.Sockets;
using System.Text;
using JALib.Tools.ByteTool;

namespace JALib.Tools;

public class JATcpClient : TcpClient
{
    private readonly bool _autoConnect;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private NetworkStream? _stream;
    private JAction? _read;
    private JAction? _onClose;
    private JAction? _onConnect;
    private Task? _readPump;

    public JATcpClient(IPEndPoint localEP, JAction? read = null, bool autoConnect = true)
        : base(localEP)
    {
        _read = read;
        _autoConnect = autoConnect;
        if (Connected)
            CompleteConnect();
    }

    public JATcpClient(JAction? read = null, bool autoConnect = true)
    {
        _read = read;
        _autoConnect = autoConnect;
    }

    public JATcpClient(AddressFamily family, JAction? read = null, bool autoConnect = true)
        : base(family)
    {
        _read = read;
        _autoConnect = autoConnect;
    }

    public JATcpClient(
        string hostname,
        int port,
        JAction? read = null,
        bool autoConnect = true)
        : this(read, autoConnect)
        => Connect(hostname, port);

    public JATcpClient(
        string hostname,
        string service,
        JAction? read = null,
        bool autoConnect = true)
        : this(read, autoConnect)
        => Connect(hostname, service);

    public JATcpClient(
        string hostname,
        int port,
        string service,
        bool onlyThisPort = false,
        JAction? read = null,
        bool autoConnect = true)
        : this(read, autoConnect)
        => Connect(hostname, port, service, onlyThisPort);

    public new void Connect(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port));
        while (true)
        {
            try
            {
                base.Connect(host, port);
                CompleteConnect();
                return;
            }
            catch when (_autoConnect && !MainThread.IsMainThread())
            {
                if (_pumpCancellation.Token.WaitHandle.WaitOne(TimeSpan.FromMinutes(1)))
                    throw new OperationCanceledException(_pumpCancellation.Token);
            }
            catch (Exception exception) when (_autoConnect && MainThread.IsMainThread())
            {
                throw new InvalidOperationException(
                    "Main thread cannot AutoConnect.",
                    exception);
            }
        }
    }

    public void Connect(string host, string service)
        => Connect(host, -1, service);

    public void Connect(
        string host,
        int port,
        string service,
        bool onlyThisPort = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        if (port < 0)
        {
            throw new NotSupportedException(
                $"SRV-only TCP service discovery is unavailable on this runtime: _{service}._tcp.{host}");
        }
        Connect(host, port);
    }

    public void SetConnectAction(JAction action)
        => _onConnect = action;

    public void SetCloseAction(JAction action)
    {
        _onClose = action;
        StartReadPump();
    }

    public byte ReadByte()
    {
        var value = RequireStream().ReadByte();
        if (value < 0)
            throw new EndOfStreamException();
        return (byte)value;
    }

    public short ReadShort() => ReadBytes(2).ToShort();
    public int ReadInt() => ReadBytes(4).ToInt();
    public long ReadLong() => ReadBytes(8).ToLong();
    public float ReadFloat() => ReadBytes(4).ToFloat();
    public double ReadDouble() => ReadBytes(8).ToDouble();
    public bool ReadBoolean() => ReadByte() != 0;
    public byte[] ReadBytesAndCount() => ReadBytes(ReadInt());
    public string ReadUTF() => Encoding.UTF8.GetString(ReadBytesAndCount());

    public byte[] ReadBytes(int count, bool force = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var buffer = new byte[count];
        if (count == 0)
            return buffer;
        var stream = RequireStream();
        var offset = 0;
        do
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        } while (force && offset < count);
        if (offset != count)
            throw new InvalidOperationException("Failed to read bytes.");
        return buffer;
    }

    public async Task<byte[]> ReadAsyncBytes(int count, bool force = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var buffer = new byte[count];
        if (count == 0)
            return buffer;
        var stream = RequireStream();
        var offset = 0;
        do
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(offset, count - offset),
                    _pumpCancellation.Token)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        } while (force && offset < count);
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

    public void WriteBytes(byte[] data) => RequireStream().Write(data);
    public void WriteBytesAndCount(byte[] data)
    {
        WriteInt(data.Length);
        WriteBytes(data);
    }
    public void WriteByte(byte value) => RequireStream().WriteByte(value);
    public void WriteShort(short value) => WriteBytes(value.ToBytes());
    public void WriteInt(int value) => WriteBytes(value.ToBytes());
    public void WriteLong(long value) => WriteBytes(value.ToBytes());
    public void WriteFloat(float value) => WriteBytes(value.ToBytes());
    public void WriteDouble(double value) => WriteBytes(value.ToBytes());
    public void WriteBoolean(bool value) => WriteByte((byte)(value ? 1 : 0));
    public void WriteUTF(string value) => WriteBytesAndCount(Encoding.UTF8.GetBytes(value));

    public Task WriteAsyncBytes(byte[] data)
        => RequireStream().WriteAsync(data, _pumpCancellation.Token).AsTask();
    public async Task WriteAsyncBytesAndCount(byte[] data)
    {
        await WriteAsyncInt(data.Length).ConfigureAwait(false);
        await WriteAsyncBytes(data).ConfigureAwait(false);
    }
    public Task WriteAsyncByte(byte value) => WriteAsyncBytes([value]);
    public Task WriteAsyncShort(short value) => WriteAsyncBytes(value.ToBytes());
    public Task WriteAsyncInt(int value) => WriteAsyncBytes(value.ToBytes());
    public Task WriteAsyncLong(long value) => WriteAsyncBytes(value.ToBytes());
    public Task WriteAsyncFloat(float value) => WriteAsyncBytes(value.ToBytes());
    public Task WriteAsyncDouble(double value) => WriteAsyncBytes(value.ToBytes());
    public Task WriteAsyncBoolean(bool value) => WriteAsyncByte((byte)(value ? 1 : 0));
    public Task WriteAsyncUTF(string value)
        => WriteAsyncBytesAndCount(Encoding.UTF8.GetBytes(value));

    public new void Close() => Dispose();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pumpCancellation.Cancel();
            _stream?.Dispose();
            _pumpCancellation.Dispose();
        }
        base.Dispose(disposing);
    }

    private void CompleteConnect()
    {
        _stream = GetStream();
        _onConnect?.Invoke();
        StartReadPump();
    }

    private NetworkStream RequireStream()
    {
        if (!Connected)
            throw new InvalidOperationException(nameof(Socket) + " is not connected");
        return _stream ??= GetStream();
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
                while (Connected && !_pumpCancellation.IsCancellationRequested)
                {
                    if (_read != null)
                        _read.Invoke();
                    else
                        await Task.Delay(100, _pumpCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_pumpCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                if (!_pumpCancellation.IsCancellationRequested)
                    _onClose?.Invoke();
            }
        });
    }
}
