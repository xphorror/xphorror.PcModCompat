namespace Xphorror.PcModCompat;

public static class PcCompatManagedIoBridge
{
    public static bool TryReadFileExactly(Stream stream, byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(buffer);
        var offset = 0;
        while (offset < buffer.Length)
        {
            var bytesRead = stream.Read(buffer, offset, buffer.Length - offset);
            if (bytesRead == 0)
                return false;
            if (bytesRead < 0)
                throw new IOException($"Stream.Read returned an invalid byte count: {bytesRead}.");
            offset += bytesRead;
        }
        return true;
    }

    public static int RequireFileReadProgress(int bytesRead)
    {
        if (bytesRead > 0)
            return bytesRead;
        if (bytesRead == 0)
            throw new EndOfStreamException(
                "A finite file-read loop reached EOF before filling its buffer.");
        throw new IOException($"Stream.Read returned an invalid byte count: {bytesRead}.");
    }
}
