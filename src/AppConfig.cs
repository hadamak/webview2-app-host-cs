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
                
                // バリデーション: 極端な値が入っている場合は補正、または失敗とする
                if (config != null)
                {
                    if (config.Width <= 0) config.Width = 1280;
                    if (config.Height <= 0) config.Height = 720;
                }
                
                return config;
            }
            catch
            {
                // JSON の構文エラーなどはここでキャッチして null を返す
                return null;
            }
        }
    }
}
