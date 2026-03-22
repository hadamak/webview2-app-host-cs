using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace WebView2AppHost
{
    /// <summary>
    /// app.conf.json の設定値。
    /// DataContract を使用して JSON とマッピングする。
    /// </summary>
    [DataContract]
    internal sealed class AppConfig
    {
        // ウィンドウサイズの許容範囲
        private const int MinSize   =  160;   // 操作可能な最小値
        private const int MaxWidth  = 7680;   // 8K 横
        private const int MaxHeight = 4320;   // 8K 縦

        [DataMember(Name = "title")]
        public string Title { get; private set; } = "WebView2 App Host";

        [DataMember(Name = "width")]
        public int Width { get; set; } = 1280;

        [DataMember(Name = "height")]
        public int Height { get; set; } = 720;

        [DataMember(Name = "fullscreen")]
        public bool Fullscreen { get; set; } = false;

        /// <summary>
        /// CORS プロキシを許可する外部オリジンのリスト。
        /// 例: ["https://api.example.com", "https://other.example.com"]
        /// 空の場合はプロキシ機能を無効とする。
        /// </summary>
        [DataMember(Name = "proxyOrigins")]
        public string[] ProxyOrigins { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// 指定した URI がプロキシ許可オリジンに含まれるかを返す。
        /// </summary>
        public bool IsProxyAllowed(Uri uri)
        {
            if (ProxyOrigins == null || ProxyOrigins.Length == 0) return false;
            var origin = uri.Scheme + "://" + uri.Host
                + (uri.IsDefaultPort ? "" : ":" + uri.Port);
            return ProxyOrigins.Any(o =>
                string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// ストリームから JSON を読み込み、AppConfig インスタンスを生成する。
        /// フォーマットエラーなどで失敗した場合は null を返す（フォールバック用）。
        /// </summary>
        public static AppConfig? Load(Stream stream)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                var config = (AppConfig?)serializer.ReadObject(stream);
                if (config != null) config.Sanitize();
                return config;
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "AppConfig.Load", "設定ファイルの読み込みに失敗（デフォルト値を使用）", ex);
                return null;
            }
        }

        /// <summary>
        /// EXE 隣接の user.conf.json を読み込み、ユーザーが上書き可能なフィールド
        /// （Width・Height・Fullscreen）を上書きする。
        /// ファイルが存在しない場合・パースに失敗した場合は何もしない。
        /// </summary>
        public void ApplyUserConfig(string exeDir)
        {
            var path = Path.Combine(exeDir, "user.conf.json");
            if (!File.Exists(path)) return;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var serializer = new DataContractJsonSerializer(typeof(UserConfig));
                var user = (UserConfig?)serializer.ReadObject(stream);
                if (user == null) return;

                if (user.Width.HasValue)
                    Width = Math.Max(MinSize, Math.Min(user.Width.Value, MaxWidth));
                if (user.Height.HasValue)
                    Height = Math.Max(MinSize, Math.Min(user.Height.Value, MaxHeight));
                if (user.Fullscreen.HasValue)
                    Fullscreen = user.Fullscreen.Value;

                AppLog.Log("INFO", "AppConfig.ApplyUserConfig", $"user.conf.json を適用: {path}");
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "AppConfig.ApplyUserConfig", "user.conf.json の読み込みに失敗（無視）", ex);
            }
        }

        /// <summary>
        /// 読み込んだ値を安全な範囲に補正する。
        /// </summary>
        private void Sanitize()
        {
            // Title: null・空文字・制御文字をデフォルトに戻す
            if (string.IsNullOrWhiteSpace(Title))
            {
                Title = "WebView2 App Host";
            }
            else
            {
                // 制御文字を除去してトリム（タイトルバーに表示する文字列として適切な形に整える）
                Title = System.Text.RegularExpressions.Regex
                    .Replace(Title, @"[\p{C}]", "")
                    .Trim();
                if (Title.Length == 0) Title = "WebView2 App Host";
            }

            // Width / Height: 範囲外はデフォルト値にクランプ
            Width  = Math.Max(MinSize, Math.Min(Width,  MaxWidth));
            Height = Math.Max(MinSize, Math.Min(Height, MaxHeight));

            // ProxyOrigins: デシリアライズ時に null になる場合があるため正規化する
            if (ProxyOrigins == null) ProxyOrigins = Array.Empty<string>();
        }
    }

    /// <summary>
    /// user.conf.json の設定値。
    /// エンドユーザーが上書き可能なフィールドのみ定義する。
    /// null は「指定なし（app.conf.json の値を使う）」を意味する。
    /// </summary>
    [DataContract]
    internal sealed class UserConfig
    {
        [DataMember(Name = "width")]
        public int? Width { get; private set; }

        [DataMember(Name = "height")]
        public int? Height { get; private set; }

        [DataMember(Name = "fullscreen")]
        public bool? Fullscreen { get; private set; }
    }
}
