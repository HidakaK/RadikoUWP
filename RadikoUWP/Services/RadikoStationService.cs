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

        // 局の週間番組表 (タイムフリー用)
        private const string WEEKLY_PROGRAM_URL = "https://radiko.jp/v3/program/station/weekly/{0}.xml";

        // stream XML が取得できない場合のフォールバック
        private const string FALLBACK_LIVE_URL = "https://si-f-radiko.smartstream.ne.jp/so/playlist.m3u8";
        private const string FALLBACK_TIMEFREE_URL = "https://tf-f-rpaa-radiko.smartstream.ne.jp/tf/playlist.m3u8";

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
        /// stream XML から playlist_create_url を取り出す (エリア内 areafree="0" を優先)。
        /// </summary>
        private async Task<List<string>> GetPlaylistBaseUrlsAsync(string stationId, bool timefree)
        {
            var baseUrls = new List<string>();
            var timefreeValue = timefree ? "1" : "0";

            try
            {
                var url = new Uri(string.Format(STREAM_INFO_URL, Uri.EscapeDataString(stationId)));
                var xmlContent = await RadikoHttp.GetStringAsync(_httpClient, url, CancellationToken.None);
                var doc = XDocument.Parse(xmlContent);

                var urls = doc.Descendants("url")
                    .Where(u => (string)u.Attribute("timefree") == timefreeValue)
                    .OrderBy(u => (string)u.Attribute("areafree") == "0" ? 0 : 1)
                    .Select(u => u.Element("playlist_create_url")?.Value?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s));

                foreach (var u in urls)
                {
                    if (!baseUrls.Contains(u))
                        baseUrls.Add(u);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"stream XML の取得に失敗: {ex.Message}");
            }

            var fallback = timefree ? FALLBACK_TIMEFREE_URL : FALLBACK_LIVE_URL;
            if (!baseUrls.Contains(fallback))
                baseUrls.Add(fallback);
            return baseUrls;
        }

        private static string AppendQuery(string baseUrl, string query)
        {
            return baseUrl + (baseUrl.Contains("?") ? "&" : "?") + query;
        }

        // lsid: HTML5 プレーヤーは MD5 形式 (16進32文字) のランダム値を使う
        private static string NewLsid()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// ライブ再生用のプレイリスト URL 候補を返す (優先度順)。
        /// 返す URL には station_id / l / lsid / type クエリが付与済み。
        /// </summary>
        public async Task<List<Uri>> GetLiveStreamUrlsAsync(string stationId)
        {
            var baseUrls = await GetPlaylistBaseUrlsAsync(stationId, false);
            var lsid = NewLsid();
            return baseUrls
                .Select(b => new Uri(AppendQuery(b, $"station_id={Uri.EscapeDataString(stationId)}&l=15&lsid={lsid}&type=b")))
                .ToList();
        }

        /// <summary>
        /// タイムフリー再生用のプレイリスト URL を作る関数の候補を返す (優先度順)。
        /// 関数の引数はシーク位置 (絶対時刻)。start_at/ft・end_at/to は番組の開始・終了時刻 (日本時間)。
        /// </summary>
        public async Task<List<Func<DateTimeOffset, Uri>>> GetTimefreeUrlBuildersAsync(string stationId, ProgramInfo program)
        {
            var baseUrls = await GetPlaylistBaseUrlsAsync(stationId, true);
            var lsid = NewLsid();
            var ft = FormatJst(program.Start);
            var to = FormatJst(program.End);

            return baseUrls
                .Select(b => (Func<DateTimeOffset, Uri>)(seek => new Uri(AppendQuery(b,
                    $"station_id={Uri.EscapeDataString(stationId)}&start_at={ft}&ft={ft}&end_at={to}&to={to}" +
                    $"&seek={FormatJst(seek)}&l=15&lsid={lsid}&type=b"))))
                .ToList();
        }

        private static string FormatJst(DateTimeOffset time)
        {
            return time.ToOffset(JST).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        #region タイムフリー番組表

        /// <summary>radiko の放送日 (5:00 区切り) を返す。</summary>
        public static string BroadcastDateOf(DateTimeOffset time)
        {
            return time.ToOffset(JST).AddHours(-5).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        /// <summary>タイムフリーで選べる日付 (今日から 7 日前まで、新しい順)。</summary>
        public static List<BroadcastDay> GetTimefreeDays()
        {
            var days = new List<BroadcastDay>();
            var today = DateTimeOffset.Now.ToOffset(JST).AddHours(-5).Date;
            for (int i = 0; i <= 7; i++)
            {
                var d = today.AddDays(-i);
                var label = $"{d:M/d} ({TimefreeProgram.DayOfWeekText(d.DayOfWeek)})";
                if (i == 0)
                    label = "今日 " + label;
                else if (i == 1)
                    label = "昨日 " + label;
                days.Add(new BroadcastDay { Date = d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), Label = label });
            }
            return days;
        }

        /// <summary>
        /// 局の週間番組表を取得し、放送日ごとの番組一覧を返す (キー: 放送日 yyyyMMdd)。
        /// </summary>
        public async Task<Dictionary<string, List<TimefreeProgram>>> GetWeeklyProgramsAsync(Station station)
        {
            var url = new Uri(string.Format(WEEKLY_PROGRAM_URL, Uri.EscapeDataString(station.Id)));
            var xmlContent = await RadikoHttp.GetStringAsync(_httpClient, url, CancellationToken.None);
            var doc = XDocument.Parse(xmlContent);

            var now = DateTimeOffset.Now;
            var oldest = now.AddDays(-7);
            var result = new Dictionary<string, List<TimefreeProgram>>();

            foreach (var progs in doc.Descendants("progs"))
            {
                foreach (var prog in progs.Elements("prog"))
                {
                    DateTimeOffset start, end;
                    if (!TryParseJst((string)prog.Attribute("ft"), out start) ||
                        !TryParseJst((string)prog.Attribute("to"), out end))
                        continue;

                    var date = progs.Element("date")?.Value?.Trim();
                    if (string.IsNullOrEmpty(date))
                        date = BroadcastDateOf(start);

                    TimefreeAvailability availability;
                    if (start > now)
                        availability = TimefreeAvailability.NotYetBroadcast;
                    else if (end > now)
                        availability = TimefreeAvailability.OnAir;
                    else if (start < oldest)
                        availability = TimefreeAvailability.Expired;
                    else if (IsTimefreeNg(prog))
                        availability = TimefreeAvailability.NotAllowed;
                    else
                        availability = TimefreeAvailability.Available;

                    List<TimefreeProgram> list;
                    if (!result.TryGetValue(date, out list))
                    {
                        list = new List<TimefreeProgram>();
                        result[date] = list;
                    }

                    list.Add(new TimefreeProgram
                    {
                        Station = station,
                        BroadcastDate = date,
                        Availability = availability,
                        Info = new ProgramInfo
                        {
                            Title = prog.Element("title")?.Value?.Trim(),
                            Performer = prog.Element("pfm")?.Value?.Trim(),
                            Start = start,
                            End = end
                        }
                    });
                }
            }

            foreach (var list in result.Values)
                list.Sort((a, b) => a.Info.Start.CompareTo(b.Info.Start));
            return result;
        }

        /// <summary>
        /// エリア内タイムフリーの不可フラグ (ts_in_ng)。0 = 可。
        /// 値の意味は公開されていないため、2 (配信なし) を不可として扱う。
        /// </summary>
        private static bool IsTimefreeNg(XElement prog)
        {
            var ng = prog.Element("ts_in_ng")?.Value?.Trim();
            return ng == "2";
        }

        #endregion
    }
}
