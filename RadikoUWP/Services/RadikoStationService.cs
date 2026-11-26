using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using RadikoUWP.Models;
using Windows.Web.Http;

namespace RadikoUWP.Services
{
    public class RadikoStationService
    {
        private const string STATION_LIST_URL = "https://radiko.jp/v3/station/list/{0}.xml";

        // エリア内の各局で現在放送中の番組
        private const string NOW_PROGRAM_URL = "https://radiko.jp/v3/program/now/{0}.xml";

        // 番組表の日時は日本時間
        private static readonly TimeSpan JST = TimeSpan.FromHours(9);

        // 放送局ごとのストリーム URL 一覧 (HTML5 プレーヤーと同じ情報源)
        private const string STREAM_INFO_URL = "https://radiko.jp/v3/station/stream/pc_html5/{0}.xml";

        // stream XML が取得できない場合のフォールバック
        private const string FALLBACK_LIVE_URL = "https://si-f-radiko.smartstream.ne.jp/so/playlist.m3u8";

        private static readonly HttpClient _httpClient = RadikoHttp.CreateClient();

        public async Task<List<Station>> GetStationsAsync(string areaId)
        {
            var stations = new List<Station>();

            var url = new Uri(string.Format(STATION_LIST_URL, areaId));
            var xmlContent = await RadikoHttp.GetStringAsync(_httpClient, url, CancellationToken.None);
            var doc = XDocument.Parse(xmlContent);

            foreach (var stationElement in doc.Descendants("station"))
            {
                var id = stationElement.Element("id")?.Value;
                var name = stationElement.Element("name")?.Value;

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                {
                    stations.Add(new Station
                    {
                        Id = id,
                        Name = name,
                        AreaId = areaId,
                        LogoUrl = SelectLogoUrl(stationElement)
                    });
                }
            }

            return stations;
        }

        /// <summary>
        /// ロゴ要素 (&lt;logo width="..." height="..."&gt;URL&lt;/logo&gt;) のうち横幅 224 前後のものを選ぶ。
        /// </summary>
        private static string SelectLogoUrl(XElement stationElement)
        {
            string best = null;
            int bestScore = int.MaxValue;
            foreach (var logo in stationElement.Elements("logo"))
            {
                var url = logo.Value?.Trim();
                if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                int width;
                if (!int.TryParse((string)logo.Attribute("width"), out width))
                    width = 0;
                int score = Math.Abs(width - 224);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = url;
                }
            }
            return best;
        }

        /// <summary>
        /// エリア内の各局で現在放送中の番組を取得する (キー: 局 ID)。取得失敗時は空。
        /// </summary>
        public async Task<Dictionary<string, ProgramInfo>> GetNowProgramsAsync(string areaId)
        {
            var result = new Dictionary<string, ProgramInfo>();
            try
            {
                var url = new Uri(string.Format(NOW_PROGRAM_URL, areaId));
                var xmlContent = await RadikoHttp.GetStringAsync(_httpClient, url, CancellationToken.None);
                var doc = XDocument.Parse(xmlContent);
                var now = DateTimeOffset.Now;

                foreach (var stationElement in doc.Descendants("station"))
                {
                    var id = (string)stationElement.Attribute("id");
                    if (string.IsNullOrEmpty(id))
                        continue;

                    ProgramInfo current = null;
                    foreach (var prog in stationElement.Descendants("prog"))
                    {
                        DateTimeOffset start, end;
                        if (!TryParseJst((string)prog.Attribute("ft"), out start) ||
                            !TryParseJst((string)prog.Attribute("to"), out end))
                            continue;

                        var info = new ProgramInfo
                        {
                            Title = prog.Element("title")?.Value?.Trim(),
                            Performer = prog.Element("pfm")?.Value?.Trim(),
                            Start = start,
                            End = end
                        };

                        if (current == null)
                            current = info;
                        if (start <= now && now < end)
                        {
                            current = info;
                            break;
                        }
                    }

                    if (current != null)
                        result[id] = current;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"番組情報の取得に失敗: {ex.Message}");
            }
            return result;
        }

        private static bool TryParseJst(string text, out DateTimeOffset value)
        {
            DateTime dt;
            if (!string.IsNullOrEmpty(text) &&
                DateTime.TryParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                value = new DateTimeOffset(dt, JST);
                return true;
            }
            value = default(DateTimeOffset);
            return false;
        }

        /// <summary>
        /// ライブ再生用のプレイリスト URL 候補を返す (優先度順)。
        /// 返す URL には station_id / l / lsid / type クエリが付与済み。
        /// </summary>
        public async Task<List<Uri>> GetLiveStreamUrlsAsync(string stationId)
        {
            var baseUrls = new List<string>();

            try
            {
                var url = new Uri(string.Format(STREAM_INFO_URL, Uri.EscapeDataString(stationId)));
                var xmlContent = await RadikoHttp.GetStringAsync(_httpClient, url, CancellationToken.None);
                var doc = XDocument.Parse(xmlContent);

                // timefree="0" (ライブ) かつ areafree="0" (エリア内) を優先
                var liveUrls = doc.Descendants("url")
                    .Where(u => (string)u.Attribute("timefree") == "0")
                    .OrderBy(u => (string)u.Attribute("areafree") == "0" ? 0 : 1)
                    .Select(u => u.Element("playlist_create_url")?.Value?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s));

                foreach (var u in liveUrls)
                {
                    if (!baseUrls.Contains(u))
                        baseUrls.Add(u);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"stream XML の取得に失敗: {ex.Message}");
            }

            if (!baseUrls.Contains(FALLBACK_LIVE_URL))
                baseUrls.Add(FALLBACK_LIVE_URL);

            // lsid: HTML5 プレーヤーは MD5 形式 (16進32文字) のランダム値を使う
            var lsid = Guid.NewGuid().ToString("N");

            return baseUrls
                .Select(b => new Uri(
                    $"{b}{(b.Contains("?") ? "&" : "?")}station_id={Uri.EscapeDataString(stationId)}&l=15&lsid={lsid}&type=b"))
                .ToList();
        }
    }
}
