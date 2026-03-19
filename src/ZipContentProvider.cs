using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace WebView2AppHost
{
    /// <summary>
    /// ZIP・ディレクトリからコンテンツを提供する。
    /// 起動時に全展開せず、リクエストのあったエントリだけ展開して返す（リクエスト時展開）。
    ///
    /// コンテンツの読み込み優先順位（高い順）:
    ///   1. 個別配置: EXE 隣接の www/ フォルダ
    ///   2. 外部指定: コマンドライン引数でパスを渡す
    ///   3. 同封: EXE と同名の .zip ファイル
    ///   4. 連結: EXE 末尾に結合された ZIP（WZGM トレーラー）
    ///   5. 埋め込み: EXE に埋め込まれたリソース（app.zip）
    ///
    /// ファイル単位でフォールバックするため、優先順位の高いソースにファイルがない場合は
    /// 次のソースを自動的に探しに行きます。
    /// </summary>
    internal sealed class ZipContentProvider : IDisposable
    {
        // 有効なコンテンツソースのリスト（優先順位順）
        private readonly List<IContentSource> _sources = new List<IContentSource>();

        private readonly string? _mockExePath;

        internal ZipContentProvider(string? mockExePath = null)
        {
            _mockExePath = mockExePath;
        }

        // ---------------------------------------------------------------------------
        // 初期化
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 優先順位に従ってすべての有効なコンテンツソースを登録する。
        /// いずれかのソースが一つでも見つかれば true を返す。
        /// </summary>
        public bool Load()
        {
            // 1. 個別配置（最優先）
            LoadIndividualSource();

            // 2〜5. 各種 ZIP ソース（見つかったものすべてをリストに追加）
            TryAddArgSource();
            TryAddSiblingSource();
            TryAddBundledSource();
            TryAddEmbeddedSource();

            return _sources.Count > 0;
        }

        private void LoadIndividualSource()
        {
            var exeDir = Path.GetDirectoryName(GetExePath()) ?? ".";
            var wwwDir = Path.Combine(exeDir, "www");
            if (Directory.Exists(wwwDir))
            {
                _sources.Add(new DirectorySource(wwwDir));
                Console.WriteLine($"[ZipContentProvider] Mounted Individual Source: {wwwDir}");
            }
        }

        private void TryAddArgSource()
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length >= 2 && File.Exists(args[1]))
            {
                var src = ZipSource.FromFile(args[1]);
                if (src != null) { _sources.Add(src); Console.WriteLine($"[ZipContentProvider] Mounted Arg Source: {args[1]}"); }
            }
        }

        private void TryAddSiblingSource()
        {
            var zipPath = Path.ChangeExtension(GetExePath(), ".zip");
            if (File.Exists(zipPath))
            {
                var src = ZipSource.FromFile(zipPath);
                if (src != null) { _sources.Add(src); Console.WriteLine($"[ZipContentProvider] Mounted Sibling Source: {zipPath}"); }
            }
        }

        private void TryAddBundledSource()
        {
            var exePath = GetExePath();
            try
            {
                var src = ZipSource.FromAppendedFile(exePath);
                if (src != null) { _sources.Add(src); Console.WriteLine("[ZipContentProvider] Mounted Bundled Source"); }
            }
            catch (Exception ex) { AppLog.Warn("ZipContentProvider.TryAddBundledSource", "Appended ZIP の検出に失敗", ex); }
        }

        private void TryAddEmbeddedSource()
        {
            var asm          = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetName().Name + ".app.zip";
            var stream       = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                var src = ZipSource.FromStream(stream);
                if (src != null) { _sources.Add(src); Console.WriteLine("[ZipContentProvider] Mounted Embedded Source"); }
                else { stream.Dispose(); }
            }
        }

        // ---------------------------------------------------------------------------
        // コンテンツ提供
        // ---------------------------------------------------------------------------

        public Stream? OpenEntry(string virtualPath)
        {
            // 優先順位が高い順に検索し、最初に見つかったものを返す
            foreach (var source in _sources)
            {
                var stream = source.OpenEntry(virtualPath);
                if (stream != null) return stream;
            }
            return null;
        }

        public byte[]? TryGetBytes(string virtualPath)
        {
            using var stream = OpenEntry(virtualPath);
            if (stream == null) return null;
            if (stream is MemoryStream ms) return ms.ToArray();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        public void Dispose()
        {
            foreach (var source in _sources) source.Dispose();
            _sources.Clear();
        }

        private string GetExePath()
            => _mockExePath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;

        // ---------------------------------------------------------------------------
        // コンテンツソース定義
        // ---------------------------------------------------------------------------

        private interface IContentSource : IDisposable
        {
            Stream? OpenEntry(string virtualPath);
        }

        /// <summary>
        /// ディレクトリソース (個別配置用)
        /// </summary>
        private sealed class DirectorySource : IContentSource
        {
            private readonly string _root;
            public DirectorySource(string root) { _root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }

            public Stream? OpenEntry(string virtualPath)
            {
                var relative = virtualPath.TrimStart('/');
                if (string.IsNullOrEmpty(relative)) return null;
                var fullPath = Path.GetFullPath(Path.Combine(_root, relative));
                if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                if (!File.Exists(fullPath)) return null;

                return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            }
            public void Dispose() { }
        }

        /// <summary>
        /// ZIP ソース (外部・同封・連結・埋め込み用)
        /// </summary>
        private sealed class ZipSource : IContentSource
        {
            private readonly ZipArchive _archive;

            private ZipSource(Stream stream)
            {
                _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            }

            public static ZipSource? FromFile(string path)
            {
                FileStream? fs = null;
                try
                {
                    fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return new ZipSource(fs);
                }
                catch (Exception ex)
                {
                    fs?.Dispose();
                    AppLog.Warn("ZipSource.FromFile", $"ZIP を開けませんでした: {path}", ex);
                    return null;
                }
            }

            public static ZipSource? FromAppendedFile(string path)
            {
                var stream = FindAppendedZipStream(path);
                if (stream == null) return null;
                return FromStream(stream);
            }

            private static Stream? FindAppendedZipStream(string path)
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    if (fs.Length < 22) { fs.Dispose(); return null; }

                    // EOCD max size is 22 + 65535 = 65557 bytes.
                    long readStart = Math.Max(0, fs.Length - 65557);
                    int readLen = (int)(fs.Length - readStart);
                    fs.Seek(readStart, SeekOrigin.Begin);

                    byte[] buffer = new byte[readLen];
                    int bytesRead = 0;
                    while (bytesRead < readLen)
                    {
                        int r = fs.Read(buffer, bytesRead, readLen - bytesRead);
                        if (r == 0) break;
                        bytesRead += r;
                    }

                    // Search backwards for EOCD signature: 0x06054b50 (PK\x05\x06)
                    // In little-endian: 0x50, 0x4B, 0x05, 0x06
                    int eocdIdx = -1;
                    for (int i = bytesRead - 22; i >= 0; i--)
                    {
                        if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
                        {
                            int commentLen = BitConverter.ToUInt16(buffer, i + 20);
                            if (i + 22 + commentLen == bytesRead)
                            {
                                eocdIdx = i;
                                break;
                            }
                        }
                    }

                    if (eocdIdx < 0)
                    {
                        fs.Dispose();
                        return null;
                    }

                    uint cdSize = BitConverter.ToUInt32(buffer, eocdIdx + 12);
                    uint cdOffset = BitConverter.ToUInt32(buffer, eocdIdx + 16);
                    uint commentLenFound = BitConverter.ToUInt16(buffer, eocdIdx + 20);

                    long totalZipSize = cdOffset + cdSize + 22 + commentLenFound;
                    if (totalZipSize > fs.Length || totalZipSize <= 0)
                    {
                        fs.Dispose();
                        return null;
                    }

                    long zipStart = fs.Length - totalZipSize;
                    return new SubStream(fs, zipStart, totalZipSize);
                }
                catch (Exception ex)
                {
                    fs.Dispose();
                    AppLog.Warn("ZipSource.FindAppendedZipStream", "Appended ZIP ストリームの検出に失敗", ex);
                    return null;
                }
            }

            public static ZipSource? FromStream(Stream stream)
            {
                try { return new ZipSource(stream); }
                catch (Exception ex)
                {
                    stream.Dispose();
                    AppLog.Warn("ZipSource.FromStream", "ストリームを ZIP として開けませんでした", ex);
                    return null;
                }
            }

            public Stream? OpenEntry(string virtualPath)
            {
                var entryName = virtualPath.TrimStart('/');
                var entry     = _archive.GetEntry(entryName);
                if (entry == null) return null;

                // entry.Open() は呼び出しのたびに新しい解凍ストリームを返す
                var stream = entry.Open();
                if (stream == null) return null;

                return new ReadOnlyStream(stream, entry.Length);
            }

            public void Dispose() { _archive.Dispose(); }
        }

        /// <summary>
        /// シーク不可な解凍ストリームに Length を付与するラッパー。
        /// WebView2 のリソースレスポンス作成に必要。
        /// </summary>
        private sealed class ReadOnlyStream : Stream
        {
            private readonly Stream _inner;
            private readonly long   _length;
            private          long   _position = 0;

            public ReadOnlyStream(Stream inner, long length)
            {
                _inner  = inner ?? throw new ArgumentNullException(nameof(inner));
                _length = length;
            }

            public override bool CanRead  => true;
            public override bool CanSeek  => false;
            public override bool CanWrite => false;
            public override long Length   => _length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                _position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// ZipArchive 用のシーク可能な部分ストリームラッパー。
        /// </summary>
        private sealed class SubStream : Stream
        {
            private readonly Stream _base;
            private readonly long _offset;
            private readonly long _length;
            private long _position;

            public SubStream(Stream baseStream, long offset, long length)
            {
                _base = baseStream;
                _offset = offset;
                _length = length;
            }

            public override bool CanRead => _base.CanRead;
            public override bool CanSeek => _base.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _position;
                set => _position = Math.Max(0, Math.Min(value, _length));
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                long remaining = _length - _position;
                if (remaining <= 0) return 0;
                if (count > remaining) count = (int)remaining;

                _base.Seek(_offset + _position, SeekOrigin.Begin);
                int read = _base.Read(buffer, offset, count);
                _position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin: _position = offset; break;
                    case SeekOrigin.Current: _position += offset; break;
                    case SeekOrigin.End: _position = _length + offset; break;
                }
                _position = Math.Max(0, Math.Min(_position, _length));
                return _position;
            }

            public override void Flush() => _base.Flush();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _base.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
