using System;
using System.Diagnostics;
using System.Text;
using RadikoUWP.Models;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace RadikoUWP.Services
{
    /// <summary>
    /// ライブ タイルの更新。通知キューで 2 枚を交互に表示する。
    ///   1 枚目: 局名 + 番組名
    ///   2 枚目: 番組情報 (放送時間・出演者・概要)
    ///
    /// タイルとバッジのガイドライン:
    /// - 該当しなくなった通知は期限切れにする → ライブは番組終了時刻で期限切れ (タイルはロゴに戻る)
    /// - キューは最大 5 件。タグで同じ枠を置き換える
    /// - タイルの内容はアプリ内でも目立つ場所 (詳細ペイン) に表示されている
    /// </summary>
    public static class LiveTileService
    {
        private const string TagStation = "station";
        private const string TagInfo = "info";

        /// <summary>
        /// 再生中 (または最後に再生した) 局と番組でタイルを更新する。
        /// </summary>
        /// <param name="isTimefree">タイムフリーの場合 true (放送日時を表示し、期限は 1 日)。</param>
        public static void Update(string stationName, ProgramInfo program, bool isTimefree)
        {
            try
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                updater.EnableNotificationQueue(true);
                updater.Clear();

                // 期限: ライブは番組終了まで、タイムフリーは 1 日 (古い情報を残さない)
                DateTimeOffset? expiration = null;
                if (program != null)
                    expiration = isTimefree ? DateTimeOffset.Now.AddDays(1) : program.End;
                else
                    expiration = DateTimeOffset.Now.AddHours(1);
                if (expiration <= DateTimeOffset.Now)
                    return;

                var first = new TileNotification(BuildStationTile(stationName, program, isTimefree))
                {
                    Tag = TagStation,
                    ExpirationTime = expiration
                };
                updater.Update(first);

                if (program != null)
                {
                    var second = new TileNotification(BuildInfoTile(stationName, program, isTimefree))
                    {
                        Tag = TagInfo,
                        ExpirationTime = expiration
                    };
                    updater.Update(second);
                }
            }
            catch (Exception ex)
            {
                // タイル更新の失敗は再生に影響させない
                Debug.WriteLine($"ライブ タイルの更新に失敗: {ex.Message}");
            }
        }

        // タイルのサイズごとに表示量を変える (システムがユーザーの選んだサイズのバインディングを使う)
        //   小 (71x71)   : 文字が少ししか入らないので局名 / 放送時間だけ
        //   中 (150x150) : 局名 + 番組名 / 時間 + 出演者 + 概要の冒頭
        //   ワイド       : 中より 1 行多く、時間と局名を 1 行に
        //   大 (PC のみ) : 番組名・出演者・概要を多めに

        /// <summary>1 枚目: 局名 + 番組名。</summary>
        public static XmlDocument BuildStationTile(string stationName, ProgramInfo program, bool isTimefree)
        {
            var title = program?.Title ?? "";
            var label = isTimefree ? "タイムフリー" : "ライブ";

            var small =
                Text(stationName, "caption", wrap: true, maxLines: 3);

            var medium =
                Text(stationName, "base", wrap: true, maxLines: 2) +
                Text(title, "captionSubtle", wrap: true, maxLines: 3);

            var wide =
                Text(stationName, "subtitle", wrap: false) +
                Text(title, "body", wrap: true, maxLines: 2) +
                Text(label, "captionSubtle", wrap: false);

            var large =
                Text(stationName, "subtitle", wrap: true, maxLines: 2) +
                Text(title, "title", wrap: true, maxLines: 3) +
                Text(program?.Performer, "body", wrap: true, maxLines: 2) +
                Text(label + (program != null ? " · " + program.TimeText : ""), "captionSubtle", wrap: false);

            return Load(small, medium, wide, large);
        }

        /// <summary>2 枚目: 番組情報 (放送時間・出演者・概要)。</summary>
        public static XmlDocument BuildInfoTile(string stationName, ProgramInfo program, bool isTimefree)
        {
            var time = isTimefree
                ? $"{program.Start.ToLocalTime():M/d} {program.TimeText}"
                : program.TimeText;
            var summary = program.Summary;

            var small =
                Text($"{program.Start.ToLocalTime():HH\\:mm}", "caption", wrap: false) +
                Text($"– {program.End.ToLocalTime():HH\\:mm}", "captionSubtle", wrap: false);

            var medium =
                Text(time, "captionSubtle", wrap: false) +
                Text(program.Performer, "caption", wrap: true, maxLines: 2) +
                Text(summary, "captionSubtle", wrap: true, maxLines: 3);

            var wide =
                Text($"{time} · {stationName}", "captionSubtle", wrap: false) +
                Text(program.Performer, "caption", wrap: false) +
                Text(summary, "captionSubtle", wrap: true, maxLines: 3);

            var large =
                Text($"{time} · {stationName}", "captionSubtle", wrap: false) +
                Text(program.Title, "base", wrap: true, maxLines: 2) +
                Text(program.Performer, "caption", wrap: true, maxLines: 2) +
                Text(summary, "captionSubtle", wrap: true, maxLines: 9);

            return Load(small, medium, wide, large);
        }

        private static XmlDocument Load(string small, string medium, string wide, string large)
        {
            // 小タイルはアプリ名を表示できないため branding="none"、それ以外はアプリ名を表示
            var xml =
                "<tile><visual branding=\"name\">" +
                "<binding template=\"TileSmall\" branding=\"none\" hint-textStacking=\"center\">" + small + "</binding>" +
                "<binding template=\"TileMedium\">" + medium + "</binding>" +
                "<binding template=\"TileWide\">" + wide + "</binding>" +
                "<binding template=\"TileLarge\">" + large + "</binding>" +
                "</visual></tile>";
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc;
        }

        private static string Text(string text, string style, bool wrap, int maxLines = 0)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var sb = new StringBuilder();
            sb.Append("<text hint-style=\"").Append(style).Append('"');
            if (wrap)
                sb.Append(" hint-wrap=\"true\"");
            if (maxLines > 0)
                sb.Append(" hint-maxLines=\"").Append(maxLines).Append('"');
            sb.Append('>').Append(Escape(text)).Append("</text>");
            return sb.ToString();
        }

        private static string Escape(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
