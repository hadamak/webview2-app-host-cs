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
    /// メイン ZIP のフォールバック順（優先度が高い順）:
    ///   1. コマンドライン引数で指定された ZIP ファイル
    ///   2. EXE と同名の .zip ファイル（隣接ファイル）
    ///   3. EXE 末尾に結合された ZIP（WZGM トレーラー）
    ///   4. EXE に埋め込まれたリソース（デフォルト）
    ///
    /// 追加ソース（EXE と同じフォルダに配置するだけで自動マウント）:
    ///   www/  → ルート（https://app.local/）にマップ
    ///
    /// リクエスト時の検索順: www/（ディレクトリ）→ メイン ZIP
    /// www/ が最優先なので、ZIP のファイルをディレクトリ側で上書きできる。
    /// </summary>
    internal sealed class ZipContentProvider : IDisposable
    {
        // WZGM バンドルのトレーラーマジック（bundle.py と一致させる）
        private const string WzgmMagic  = "WZGM";
        private const int    TrailerSize = 12; // 8バイト(ZIP サイズ) + 4バイト(マジック)

        // メイン ZIP
        private ZipArchive? _archive;
        private Stream?     _stream;

        // 追加ソース（登録順に検索）
        private readonly List<IContentSource> _extras = new List<IContentSource>();

        // ---------------------------------------------------------------------------
        // 初期化
        // ---------------------------------------------------------------------------

        /// <summary>
        /// フォールバック順でメイン ZIP を開き、追加ソースを自動検出する。
        /// 優先順位（高い順）: コマンドライン引数 → 隣接 ZIP → WZGM 結合 → 埋め込みリソース
        /// </summary>
        public bool Load()
        {
            var ok = TryLoadFromArg()
                  || TryLoadFromSiblingFile()
                  || TryLoadFromSelf()
                  || TryLoadFromResource();

            if (ok) LoadExtraSources();
            return ok;
        }

        /// <summary>
        /// EXE 隣接の追加ソースを自動検出して登録する。
        /// </summary>
        private void LoadExtraSources()
        {
            var exeDir = Path.GetDirectoryName(GetExePath()) ?? ".";

            // www/ ディレクトリ → https://app.local/ にルートマップ（最優先）
            var wwwDir = Path.Combine(exeDir, "www");
            if (Directory.Exists(wwwDir))
            {
                _extras.Add(new DirectorySource(wwwDir));
                Console.WriteLine($"[ZipContentProvider] mounted: {wwwDir}/");
            }
        }

        private bool TryLoadFromArg()
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length < 2) return false;
            var path = args[1];
            if (!File.Exists(path)) return false;
            return TryOpenZipFile(path);
        }

        private bool TryLoadFromSelf()
        {
            var exePath = GetExePath();
            try
            {
                using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < TrailerSize) return false;

                fs.Seek(-TrailerSize, SeekOrigin.End);
                var trailer = new byte[TrailerSize];
                fs.Read(trailer, 0, TrailerSize);

                var magic = System.Text.Encoding.ASCII.GetString(trailer, 8, 4);
                if (magic != WzgmMagic) return false;

                var zipSize   = BitConverter.ToInt64(trailer, 0);
                var zipOffset = fs.Length - TrailerSize - zipSize;
                if (zipOffset < 0 || zipSize <= 0) return false;

                if (zipSize > int.MaxValue) return false;
                var zipBytes  = new byte[zipSize];
                fs.Seek(zipOffset, SeekOrigin.Begin);
                var totalRead = 0;
                while (totalRead < (int)zipSize)
                {
                    var n = fs.Read(zipBytes, totalRead, (int)zipSize - totalRead);
                    if (n == 0) return false;
                    totalRead += n;
                }

                return TryOpenZipStream(new MemoryStream(zipBytes));
            }
            catch { return false; }
        }

        private bool TryLoadFromSiblingFile()
        {
            var zipPath = Path.ChangeExtension(GetExePath(), ".zip");
            if (!File.Exists(zipPath)) return false;
            return TryOpenZipFile(zipPath);
        }

        private bool TryLoadFromResource()
        {
            var asm          = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetName().Name + ".app.zip";
            var stream       = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return false;
            return TryOpenZipStream(stream);
        }

        private bool TryOpenZipFile(string path)
        {
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return TryOpenZipStream(fs);
            }
            catch { return false; }
        }

        private bool TryOpenZipStream(Stream stream)
        {
            try
            {
                _stream  = stream;
                _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                return true;
            }
            catch
            {
                stream.Dispose();
                return false;
            }
        }

        // ---------------------------------------------------------------------------
        // コンテンツ提供
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 仮想パス（例: /assets/image.png）に対応する Stream を返す。
        /// 呼び出し元が Stream を Dispose する責任を持つ。
        /// エントリが存在しない場合は null を返す。
        ///
        /// 無圧縮（ZIP_STORED）エントリ: ZIP ストリーム上のデータ位置を SubStream で直接返す。
        /// 圧縮（ZIP_DEFLATED）エントリ: MemoryStream に展開して返す。
        /// </summary>
        public Stream? OpenEntry(string virtualPath)
        {
            // 追加ソースを優先して検索
            foreach (var extra in _extras)
            {
                var s = extra.OpenEntry(virtualPath);
                if (s != null) return s;
            }

            // メイン ZIP を検索
            return OpenFromArchive(virtualPath);
        }

        private Stream? OpenFromArchive(string virtualPath)
        {
            if (_archive == null || _stream == null) return null;

            var entryName = virtualPath.TrimStart('/');
            var entry     = _archive.GetEntry(entryName);
            if (entry == null) return null;

            // 無圧縮エントリはシーク可能な SubStream で直接返す
            if (entry.CompressedLength == entry.Length && _stream.CanSeek)
            {
                var dataOffset = GetStoredEntryDataOffset(entry, _stream);
                if (dataOffset >= 0)
                    return new SubStream(_stream, dataOffset, entry.Length, ownsInner: false);
            }

            // 圧縮エントリは MemoryStream に展開
            using var entryStream = entry.Open();
            var ms = new MemoryStream((int)entry.Length);
            entryStream.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        /// <summary>
        /// エントリ全体をバイト配列で返す（app.conf.json など小さいファイル用）。
        /// </summary>
        public byte[]? TryGetBytes(string virtualPath)
        {
            using var stream = OpenEntry(virtualPath);
            if (stream == null) return null;
            if (stream is MemoryStream ms) return ms.ToArray();
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        // ---------------------------------------------------------------------------
        // ZIP ローカルファイルヘッダ解析
        // ---------------------------------------------------------------------------

        private static long GetStoredEntryDataOffset(ZipArchiveEntry entry, Stream zipStream)
        {
            try
            {
                var field = entry.GetType().GetField(
                    "_offsetOfLocalHeader",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) return -1;

                var headerOffset = (long)field.GetValue(entry)!;

                zipStream.Seek(headerOffset, SeekOrigin.Begin);
                var header = new byte[30];
                if (zipStream.Read(header, 0, 30) < 30) return -1;

                if (header[0] != 0x50 || header[1] != 0x4B ||
                    header[2] != 0x03 || header[3] != 0x04)
                    return -1;

                var fileNameLen = BitConverter.ToUInt16(header, 26);
                var extraLen    = BitConverter.ToUInt16(header, 28);
                return headerOffset + 30 + fileNameLen + extraLen;
            }
            catch { return -1; }
        }

        // ---------------------------------------------------------------------------
        // ユーティリティ
        // ---------------------------------------------------------------------------

        private static string GetExePath()
            => System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;

        public void Dispose()
        {
            _archive?.Dispose();
            _stream?.Dispose();
            foreach (var extra in _extras) extra.Dispose();
        }

        // ---------------------------------------------------------------------------
        // コンテンツソース抽象
        // ---------------------------------------------------------------------------

        private interface IContentSource : IDisposable
        {
            Stream? OpenEntry(string virtualPath);
        }

        /// <summary>
        /// ディレクトリソース（ファイルシステム直読み）。
        /// EXE 隣の www/ フォルダを https://app.local/ にルートマップする。
        ///   www/index.html       → https://app.local/index.html
        ///   www/assets/video.mp4 → https://app.local/assets/video.mp4
        /// </summary>
        private sealed class DirectorySource : IContentSource
        {
            private readonly string _root;

            public DirectorySource(string root)
            {
                _root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            public Stream? OpenEntry(string virtualPath)
            {
                var relative = virtualPath.TrimStart('/');
                if (string.IsNullOrEmpty(relative)) return null;

                var fullPath = Path.GetFullPath(Path.Combine(_root, relative));

                // ディレクトリトラバーサル防止
                if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    return null;

                if (!File.Exists(fullPath)) return null;

                return new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.RandomAccess);
            }

            public void Dispose() { }
        }
    }
}
