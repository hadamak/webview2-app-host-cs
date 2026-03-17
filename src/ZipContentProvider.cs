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
        private const string WzgmMagic  = "WZGM";
        private const int    TrailerSize = 12;

        // 有効なコンテンツソースのリスト（優先順位順）
        private readonly List<IContentSource> _sources = new List<IContentSource>();

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
                var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < TrailerSize) { fs.Dispose(); return; }

                fs.Seek(-TrailerSize, SeekOrigin.End);
                var trailer = new byte[TrailerSize];
                fs.Read(trailer, 0, TrailerSize);

                if (System.Text.Encoding.ASCII.GetString(trailer, 8, 4) != WzgmMagic) { fs.Dispose(); return; }

                var zipSize   = BitConverter.ToInt64(trailer, 0);
                var zipOffset = fs.Length - TrailerSize - zipSize;
                if (zipOffset < 0 || zipSize <= 0) { fs.Dispose(); return; }

                var sub = new SubStream(fs, zipOffset, zipSize, ownsInner: true);
                var src = ZipSource.FromStream(sub);
                if (src != null) { _sources.Add(src); Console.WriteLine("[ZipContentProvider] Mounted Bundled Source"); }
            }
            catch { /* Ignore */ }
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

        private static string GetExePath()
            => System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;

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
                try { return new ZipSource(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
                catch { return null; }
            }

            public static ZipSource? FromStream(Stream stream)
            {
                try { return new ZipSource(stream); }
                catch { return null; }
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
    }
}
