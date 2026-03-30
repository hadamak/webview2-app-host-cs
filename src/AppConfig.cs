using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

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
        private const int MinSize   =  160;
        private const int MaxWidth  = 7680;
        private const int MaxHeight = 4320;

        // ⑤ 修正: Sanitize で毎回インスタンス化していた Regex を static readonly に昇格する。
        // Compiled を付与することで初回以降の呼び出しコストを削減する。
        private static readonly Regex s_controlCharRegex =
            new Regex(@"[\p{C}]", RegexOptions.Compiled);

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
        /// 読み込むプラグイン名のリスト。
        /// 例: ["Steam", "Node"]
        /// 省略した場合は EXE 隣接の WebView2AppHost.*.dll を自動検出する。
        /// </summary>
        [DataMember(Name = "plugins")]
        public string[] Plugins { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Steam AppID。Steamworks 機能を使う場合に設定する。
        /// 空または未設定の場合は steam_appid.txt または Steam 起動時の自動検出に委ねる。
        /// </summary>
        [DataMember(Name = "steamAppId")]
        public string SteamAppId { get; private set; } = "";

        /// <summary>
        /// Steam 開発モードフラグ。
        /// true の場合、SteamAppId を環境変数経由で Steam に渡す（steam_appid.txt 不要）。
        /// false の場合、SteamAPI_RestartAppIfNecessary() を呼んで Steam から起動されているか確認する。
        /// リリースビルドでは false に設定すること。
        /// </summary>
        [DataMember(Name = "steamDevMode")]
        public bool SteamDevMode { get; private set; } = true;

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
                // ⑤ static readonly の Regex を使用する（毎回インスタンス化を回避）
                Title = s_controlCharRegex.Replace(Title, "").Trim();
                if (Title.Length == 0) Title = "WebView2 App Host";
            }

            // Width / Height: 範囲外はデフォルト値にクランプ
            Width  = Math.Max(MinSize, Math.Min(Width,  MaxWidth));
            Height = Math.Max(MinSize, Math.Min(Height, MaxHeight));

            // ProxyOrigins: デシリアライズ時に null になる場合があるため正規化する
            if (ProxyOrigins == null) ProxyOrigins = Array.Empty<string>();

            // Plugins: null を空配列に正規化する
            if (Plugins == null) Plugins = Array.Empty<string>();

            // SteamAppId: null は空文字に正規化
            if (SteamAppId == null) SteamAppId = "";
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
