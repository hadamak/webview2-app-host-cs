using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace WebView2AppHost
{
    /// <summary>
    /// ZIP・ディレクトリからコンテンツを提供する。
    /// 起動時に全展開は行わず、必要なエントリだけを要求時に読み出す。
    /// ZIP エントリは小さいファイルのみメモリにキャッシュし、大きなファイルは都度展開する。
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
                AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", $"Mounted Individual Source: {wwwDir}");
            }
        }

        private void TryAddArgSource()
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length >= 2 && File.Exists(args[1]))
            {
                var src = ZipSource.FromFile(args[1]);
                if (src != null) { _sources.Add(src); AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", $"Mounted Arg Source: {args[1]}"); }
            }
        }

        private void TryAddSiblingSource()
        {
            var zipPath = Path.ChangeExtension(GetExePath(), ".zip");
            if (File.Exists(zipPath))
            {
                var src = ZipSource.FromFile(zipPath);
                if (src != null) { _sources.Add(src); AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", $"Mounted Sibling Source: {zipPath}"); }
            }
        }

        private void TryAddBundledSource()
        {
            var exePath = GetExePath();
            try
            {
                var src = ZipSource.FromAppendedFile(exePath);
                if (src != null) { _sources.Add(src); AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", "Mounted Bundled Source"); }
            }
            catch (Exception ex) { AppLog.Log(AppLog.LogLevel.Error, "ZipContentProvider.TryAddBundledSource", "Appended ZIP の検出に失敗", ex); }
        }

        private void TryAddEmbeddedSource()
        {
            var asm          = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetName().Name + ".app.zip";
            var stream       = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                var src = ZipSource.FromStream(stream);
                if (src != null) { _sources.Add(src); AppLog.Log(AppLog.LogLevel.Info, "ZipContentProvider", "Mounted Embedded Source"); }
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
            private const int MaxCachedEntryBytes = 1024 * 1024;

            private readonly ZipArchive _archive;
            private readonly Dictionary<string, byte[]> _cache =
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

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
                    AppLog.Log(AppLog.LogLevel.Error, "ZipSource.FromFile", $"ZIP を開けませんでした: {path}", ex);
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
                    return new SubStream(fs, zipStart, totalZipSize);
                }
                catch (Exception ex)
                {
                    fs.Dispose();
                    AppLog.Log(AppLog.LogLevel.Error, "ZipSource.FindAppendedZipStream", "Appended ZIP ストリームの検出に失敗", ex);
                    return null;
                }
            }

            public static ZipSource? FromStream(Stream stream)
            {
                try { return new ZipSource(stream); }
                catch (Exception ex)
                {
                    stream.Dispose();
                    AppLog.Log(AppLog.LogLevel.Error, "ZipSource.FromStream", "ストリームを ZIP として開けませんでした", ex);
                    return null;
                }
            }

            public Stream? OpenEntry(string virtualPath)
            {
                var entryName = virtualPath.TrimStart('/');
                if (_cache.TryGetValue(entryName, out var cached))
                    return new MemoryStream(cached, writable: false);

                var entry     = _archive.GetEntry(entryName);
                if (entry == null) return null;

                // ④ 修正: entry.Length が int の範囲を超える場合に明示的なエラーを返す。
                // 2GB を超えるファイルは (int)entry.Length がオーバーフローし、
                // MemoryStream の初期容量が負値になって ArgumentOutOfRangeException が発生する。
                // 動画・音声など大きなファイルは www/ フォルダへの個別配置を推奨する。
                if (entry.Length > int.MaxValue)
                {
                    AppLog.Log(AppLog.LogLevel.Warn, "ZipSource.OpenEntry",
                    $"エントリが大きすぎるため ZIP からの展開をスキップします: {entry.FullName} ({entry.Length:N0} bytes)。" +
                        "動画・音声など大きなファイルは www/ フォルダへの個別配置を推奨します。");
                    return null;
                }

                // エントリ全体を MemoryStream に展開して返す。
                // シーク可能になるため Range Request に対応できる。
                // 大きなファイル（動画・音声等）はメモリを圧迫するため www/ への個別配置を推奨。
                using var entryStream = entry.Open();
                var ms = new MemoryStream((int)entry.Length);
                entryStream.CopyTo(ms);
                ms.Position = 0;

                if (entry.Length <= MaxCachedEntryBytes)
                    _cache[entryName] = ms.ToArray();

                return ms;
            }

            public void Dispose()
            {
                _cache.Clear();
                _archive.Dispose();
            }
        }

    }
}
