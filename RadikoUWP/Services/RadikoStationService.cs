using System;
using System.Collections.Generic;
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
                        AreaId = areaId
                    });
                }
            }

            return stations;
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
