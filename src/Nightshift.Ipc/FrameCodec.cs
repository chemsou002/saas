using System.Buffers.Binary;

namespace Nightshift.Ipc;

public sealed class IpcProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Frames: 4-byte little-endian payload length, then UTF-8 JSON. Length is bounded.</summary>
public static class FrameCodec
{
    /// <returns>The frame, or null if the peer closed the connection cleanly before a new frame.</returns>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var read = await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }
        if (read < 4)
        {
            throw new IpcProtocolException(IpcErrorCodes.MalformedRequest, "Truncated frame header.");
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxBytes)
        {
            throw new IpcProtocolException(IpcErrorCodes.FrameTooLarge, $"Frame length {length} is outside 1..{maxBytes}.");
        }
        var body = new byte[length];
        if (await ReadExactlyOrEofAsync(stream, body, ct).ConfigureAwait(false) < length)
        {
            throw new IpcProtocolException(IpcErrorCodes.MalformedRequest, "Truncated frame body.");
        }
        return body;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (body.Length is 0 or > IpcDefaults.MaxFrameBytes)
        {
            throw new IpcProtocolException(IpcErrorCodes.FrameTooLarge, "Outgoing frame too large.");
        }
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame.AsMemory(4));
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactlyOrEofAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }
}
