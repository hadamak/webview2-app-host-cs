using System;
using System.IO;
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
        public int Width { get; private set; } = 1280;

        [DataMember(Name = "height")]
        public int Height { get; private set; } = 720;

        [DataMember(Name = "fullscreen")]
        public bool Fullscreen { get; private set; } = false;

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
        }
    }
}
