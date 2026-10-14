using System.Text.Json;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>输入与输出诊断共用的有界前缀写出；统计完整编码长度，不分配完整序列化副本。</summary>
internal static class DiagnosticSnapshotWriter
{
    internal static void WriteText(Utf8JsonWriter writer, string text)
    {
        var remaining = text.AsSpan();
        while (remaining.Length > 512)
        {
            writer.WriteStringValueSegment(remaining[..512], isFinalSegment: false);
            remaining = remaining[512..];
            writer.Flush();
        }
        writer.WriteStringValueSegment(remaining, isFinalSegment: true);
    }

    internal static void WriteUtf8(Utf8JsonWriter writer, ReadOnlySpan<byte> text)
    {
        while (text.Length > 512)
        {
            var length = 512;
            while ((text[length] & 0xc0) == 0x80)
            { length--; }
            writer.WriteStringValueSegment(text[..length], isFinalSegment: false);
            text = text[length..];
            writer.Flush();
        }
        writer.WriteStringValueSegment(text, isFinalSegment: true);
    }

    internal sealed class PrefixStream(bool retainBody) : Stream
    {
        private readonly byte[] _prefix = new byte[retainBody ? RuntimeMessageCache.MaximumRecordBytes : 0];
        private long _length;
        internal byte[] Prefix() => _prefix.AsSpan(0, (int)Math.Min(_length, _prefix.Length)).ToArray();
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var remaining = (int)Math.Max(0, _prefix.Length - _length);
            buffer[..Math.Min(buffer.Length, remaining)].CopyTo(_prefix.AsSpan((int)Math.Min(_length, _prefix.Length)));
            _length = checked(_length + buffer.Length);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override long Length => _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
