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
        private struct EntryMetadata
        {
            public IContentSource Source;
            public string ActualName;
            public bool IsEncrypted;
            public bool IsPlainCore;
        }

        // 有効なコンテンツソースのリスト（優先順位順: 0:www, 1:arg, 2:sibling, 3:bundled, 4:embedded）
        private readonly List<IContentSource> _sources = new List<IContentSource>();

        // パスインデックス: virtualPath -> 各ソースでのエントリ情報のリスト
        private readonly Dictionary<string, List<EntryMetadata>> _index = 
            new Dictionary<string, List<EntryMetadata>>(StringComparer.OrdinalIgnoreCase);

        private readonly string? _mockExePath;

        internal ZipContentProvider(string? mockExePath = null)
        {
            _mockExePath = mockExePath;
        }

        // ---------------------------------------------------------------------------
        // 初期化
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 優先順位に従ってすべての有効なコンテンツソースを登録し、インデックスを構築する。
        /// </summary>
        public bool Load()
        {
            _sources.Clear();
            _index.Clear();

            // 1. 個別配置（最優先）
            LoadIndividualSource();

            // 2〜5. 各種 ZIP ソース
            TryAddArgSource();
            TryAddSiblingSource();
            TryAddBundledSource();
            TryAddEmbeddedSource();

            if (_sources.Count == 0) return false;

            // インデックスの構築
            BuildIndex();

            return true;
        }

        private void BuildIndex()
        {
            // 各ソースからエントリ名を収集し、インデックスに登録する
            for (int i = 0; i < _sources.Count; i++)
            {
                var source = _sources[i];
                foreach (var actualName in source.GetPaths())
                {
                    bool isEncrypted = actualName.EndsWith(".wve", StringComparison.OrdinalIgnoreCase);
                    bool isPlainCore  = actualName.EndsWith(".wvc", StringComparison.OrdinalIgnoreCase);

                    // 仮想パス（.wve/.wvc を除いたベース名）を算出
                    string virtualPath;
                    if (isEncrypted) virtualPath = "/" + actualName.Substring(0, actualName.Length - 4).Replace('\\', '/').TrimStart('/');
                    else if (isPlainCore) virtualPath = "/" + actualName.Substring(0, actualName.Length - 4).Replace('\\', '/').TrimStart('/');
                    else virtualPath = "/" + actualName.Replace('\\', '/').TrimStart('/');

                    if (!_index.TryGetValue(virtualPath, out var list))
                    {
                        list = new List<EntryMetadata>();
                        _index[virtualPath] = list;
                    }

                    list.Add(new EntryMetadata 
                    { 
                        Source = source, 
                        ActualName = actualName, 
                        IsEncrypted = isEncrypted, 
                        IsPlainCore = isPlainCore 
                    });
                }
            }
        }

        private void LoadIndividualSource()
        {
            var exeDir = Path.GetDirectoryName(GetExePath()) ?? ".";
            var wwwDir = Path.Combine(exeDir, "www");
            if (Directory.Exists(wwwDir))
            {
                _sources.Add(new DirectorySource(wwwDir));
                AppLog.Log("INFO", "ZipContentProvider", $"Mounted Individual Source: {wwwDir}");
            }
        }

        private void TryAddArgSource()
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length >= 2 && File.Exists(args[1]))
            {
                var src = ZipSource.FromFile(args[1]);
                if (src != null) { _sources.Add(src); AppLog.Log("INFO", "ZipContentProvider", $"Mounted Arg Source: {args[1]}"); }
            }
        }

        private void TryAddSiblingSource()
        {
            var zipPath = Path.ChangeExtension(GetExePath(), ".zip");
            if (File.Exists(zipPath))
            {
                var src = ZipSource.FromFile(zipPath);
                if (src != null) { _sources.Add(src); AppLog.Log("INFO", "ZipContentProvider", $"Mounted Sibling Source: {zipPath}"); }
            }
        }

        private void TryAddBundledSource()
        {
            var exePath = GetExePath();
            try
            {
                var src = ZipSource.FromAppendedFile(exePath);
                if (src != null) { _sources.Add(src); AppLog.Log("INFO", "ZipContentProvider", "Mounted Bundled Source"); }
            }
            catch (Exception ex) { AppLog.Log("ERROR", "ZipContentProvider.TryAddBundledSource", "Appended ZIP の検出に失敗", ex); }
        }

        private void TryAddEmbeddedSource()
        {
            var asm          = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetName().Name + ".app.zip";
            var stream       = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                var src = ZipSource.FromStream(stream);
                if (src != null) { _sources.Add(src); AppLog.Log("INFO", "ZipContentProvider", "Mounted Embedded Source"); }
                else { stream.Dispose(); }
            }
        }

        // ---------------------------------------------------------------------------
        // コンテンツ提供
        // ---------------------------------------------------------------------------

        public Stream? OpenEntry(string virtualPath)
        {
            var path = "/" + virtualPath.Replace('\\', '/').TrimStart('/');
            if (!_index.TryGetValue(path, out var list)) return null;

            // 1. 保護ファイルの検索 (内側優先: リストの末尾から先頭へ)
            // .wve または .wvc があれば、それを最優先する。
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var meta = list[i];
                if (meta.IsEncrypted || meta.IsPlainCore)
                {
                    var stream = meta.Source.OpenEntry(meta.ActualName);
                    if (stream == null) continue;

                    if (meta.IsEncrypted)
                    {
                        try { return CryptoUtils.CreateDecryptStream(stream); }
                        catch (Exception ex)
                        {
                            stream.Dispose();
                            AppLog.Log("ERROR", "ZipContentProvider", $"復号失敗: {meta.ActualName}", ex);
                            continue;
                        }
                    }
                    return stream;
                }
            }

            // 2. 通常ファイルの検索 (外側優先: リストの先頭から末尾へ)
            for (int i = 0; i < list.Count; i++)
            {
                var meta = list[i];
                if (!meta.IsEncrypted && !meta.IsPlainCore)
                {
                    return meta.Source.OpenEntry(meta.ActualName);
                }
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
            IEnumerable<string> GetPaths();
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
                var fullPath = Path.GetFullPath(Path.Combine(_root, relative));
                if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                if (!File.Exists(fullPath)) return null;
                return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            }

            public IEnumerable<string> GetPaths()
            {
                if (!Directory.Exists(_root)) yield break;
                var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    yield return f.Substring(_root.Length).Replace('\\', '/').TrimStart('/');
                }
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
                    AppLog.Log("ERROR", "ZipSource.FromFile", $"ZIP を開けませんでした: {path}", ex);
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
                    // NOTE: ZIP64 EOCD (signature 0x06064b50) は非対応。
                    //       エントリ数が 65535 を超えるか、個別ファイルが 4 GB を超える場合は
                    //       正しく検出できないため、外部 ZIP ソースを使用すること。
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

                    uint cdSize   = BitConverter.ToUInt32(buffer, eocdIdx + 12);
                    uint cdOffset = BitConverter.ToUInt32(buffer, eocdIdx + 16);
                    uint commentLenFound = BitConverter.ToUInt16(buffer, eocdIdx + 20);

                    // ① 修正: uint 同士の演算を long に昇格してからオーバーフローを防ぐ。
                    // 変更前: cdOffset + cdSize は uint 演算のため 4GB 付近でオーバーフローし、
                    //         その後 long に昇格しても誤った負値になる。
                    long totalZipSize = (long)cdOffset + cdSize + 22 + commentLenFound;

                    if (totalZipSize > fs.Length || totalZipSize <= 0)
                    {
                        fs.Dispose();
                        return null;
                    }

                    long zipStart = fs.Length - totalZipSize;
                    return new OffsetStream(fs, zipStart, totalZipSize);
                }
                catch (Exception ex)
                {
                    fs.Dispose();
                    AppLog.Log("ERROR", "ZipSource.FindAppendedZipStream", "Appended ZIP ストリームの検出に失敗", ex);
                    return null;
                }
            }

            public static ZipSource? FromStream(Stream stream)
            {
                try { return new ZipSource(stream); }
                catch (Exception ex)
                {
                    stream.Dispose();
                    AppLog.Log("ERROR", "ZipSource.FromStream", "ストリームを ZIP として開けませんでした", ex);
                    return null;
                }
            }

            public Stream? OpenEntry(string virtualPath)
            {
                // インデックス化されているため actualName がそのまま渡される
                var entry = _archive.GetEntry(virtualPath);
                if (entry == null) return null;
                if (entry.Length > int.MaxValue) return null;

                using var entryStream = entry.Open();
                var ms = new MemoryStream((int)entry.Length);
                entryStream.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }

            public IEnumerable<string> GetPaths()
            {
                foreach (var entry in _archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // ディレクトリは除外
                    yield return entry.FullName;
                }
            }

            public void Dispose() { _archive.Dispose(); }
        }

        /// <summary>
        /// ZipArchive 用のシーク可能な部分ストリームラッパー。
        /// </summary>
        private sealed class OffsetStream : Stream
        {
            private readonly Stream _base;
            private readonly long _offset;
            private readonly long _length;
            private long _position;

            public OffsetStream(Stream baseStream, long offset, long length)
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
