using System.IO.Compression;

namespace AgsCopy.Core.Sync;

/// <summary>
/// The .gz files the copy engine writes: standard gzip (7-Zip, gzip and Windows 11 open them)
/// carrying the comment "AGS-Copy" in the header. A restore decompresses only files with that
/// mark, so a backup.sql.gz that was already in the source comes back exactly as it was.
/// </summary>
public static class AgsGzip
{
    public const string Mark = "AGS-Copy";
    private const byte FlagExtra = 4, FlagName = 8, FlagComment = 16;

    public static Stream Create(Stream output) => new Writer(output);

    /// <summary>True when the stream starts with a gzip header carrying the AGS-Copy comment. Reads only the header.</summary>
    public static bool IsMarked(Stream input)
    {
        Span<byte> head = stackalloc byte[10];
        if (input.ReadAtLeast(head, 10, throwOnEndOfStream: false) < 10) return false;
        if (head[0] != 0x1f || head[1] != 0x8b || head[2] != 8) return false;
        var flags = head[3];
        if ((flags & FlagComment) == 0) return false;
        if ((flags & FlagExtra) != 0)
        {
            var len = input.ReadByte() | (input.ReadByte() << 8);
            if (len < 0) return false;
            for (var i = 0; i < len; i++)
                if (input.ReadByte() < 0) return false;
        }
        if ((flags & FlagName) != 0 && ReadZeroTerminated(input, 4096) == null) return false;
        return ReadZeroTerminated(input, 64) == Mark;
    }

    private static string? ReadZeroTerminated(Stream s, int max)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < max; i++)
        {
            var b = s.ReadByte();
            if (b < 0) return null;
            if (b == 0) return System.Text.Encoding.Latin1.GetString(bytes.ToArray());
            bytes.Add((byte)b);
        }
        return null;
    }

    /// <summary>gzip member: header with the comment, raw deflate, then CRC-32 and size.</summary>
    private sealed class Writer : Stream
    {
        private readonly Stream _output;
        private readonly DeflateStream _deflate;
        private uint _crc = 0xFFFFFFFF;
        private long _length;
        private bool _closed;

        public Writer(Stream output)
        {
            _output = output;
            // ID1 ID2 CM FLG MTIME(4) XFL OS(255 = unknown), then the comment.
            output.Write([0x1f, 0x8b, 8, FlagComment, 0, 0, 0, 0, 0, 255]);
            output.Write(System.Text.Encoding.Latin1.GetBytes(Mark + "\0"));
            _deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _crc = Crc32.Update(_crc, buffer);
            _length += buffer.Length;
            _deflate.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _crc = Crc32.Update(_crc, buffer.Span);
            _length += buffer.Length;
            await _deflate.WriteAsync(buffer, ct);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                _deflate.Dispose();
                Span<byte> trailer = stackalloc byte[8];
                BitConverter.TryWriteBytes(trailer, ~_crc);
                BitConverter.TryWriteBytes(trailer[4..], (uint)_length);
                _output.Write(trailer);
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _deflate.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
