using System;
using System.IO;

namespace WebView2AppHost
{
    // ---------------------------------------------------------------------------
    // SubStream: Stream の部分範囲を別の Stream として公開（Range Request 用）
    // ---------------------------------------------------------------------------

    internal sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long   _offset;
        private readonly long   _length;
        private readonly bool   _ownsInner;
        private          long   _position;

        public SubStream(Stream inner, long offset, long length, bool ownsInner = true)
        {
            _inner     = inner;
            _offset    = offset;
            _length    = length;
            _ownsInner = ownsInner;
            _position  = 0;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => false;
        public override long Length   => _length;

        public override long Position
        {
            get { lock (_inner) return _position; }
            set
            {
                lock (_inner)
                {
                    _position = value;
                    _inner.Position = _offset + value;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_inner)
            {
                var remaining = _length - _position;
                if (remaining <= 0) return 0;
                count = (int)Math.Min(count, remaining);
                _inner.Position = _offset + _position;
                var read = _inner.Read(buffer, offset, count);
                _position += read;
                return read;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_inner)
            {
                long newPos = origin switch
                {
                    SeekOrigin.Begin   => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End     => _length + offset,
                    _                  => throw new ArgumentException()
                };
                Position = newPos;
                return _position;
            }
        }

        public override void Flush()  { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsInner) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
