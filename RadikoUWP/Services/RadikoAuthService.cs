using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Web.Http;

namespace RadikoUWP.Services
{
    public class RadikoAuthService
    {
        // radiko認証キー (HTML5 プレーヤー pc_html5 用の固定キー)
        private const string AUTH_KEY = "bcd151073c03b352e1ef2fd66c32209da9ca0afa";

        private const string AUTH1_URL = "https://radiko.jp/v2/api/auth1";
        private const string AUTH2_URL = "https://radiko.jp/v2/api/auth2";

        private const string APP_NAME = "pc_html5";
        private const string APP_VERSION = "0.0.1";
        private const string DEVICE = "pc";

        private static readonly HttpClient _httpClient = RadikoHttp.CreateClient();

        public string AuthToken { get; private set; }
        public string AreaId { get; private set; }
        public string AreaName { get; private set; }

        public bool IsAuthenticated => !string.IsNullOrEmpty(AuthToken) && !string.IsNullOrEmpty(AreaId);

        public async Task AuthenticateAsync()
        {
            AuthToken = null;
            AreaId = null;
            AreaName = null;

            var userId = Guid.NewGuid().ToString("N");

            // --- Step 1: auth1 ---
            string token;
            int keyLength;
            int keyOffset;
            using (var auth1Request = new HttpRequestMessage(HttpMethod.Get, new Uri(AUTH1_URL)))
            {
                auth1Request.Headers.TryAppendWithoutValidation("X-Radiko-App", APP_NAME);
                auth1Request.Headers.TryAppendWithoutValidation("X-Radiko-App-Version", APP_VERSION);
                auth1Request.Headers.TryAppendWithoutValidation("X-Radiko-Device", DEVICE);
                auth1Request.Headers.TryAppendWithoutValidation("X-Radiko-User", userId);

                using (var auth1Response = await _httpClient.SendRequestAsync(auth1Request))
                {
                    if (!auth1Response.IsSuccessStatusCode)
                        throw new Exception($"auth1 失敗 (HTTP {(int)auth1Response.StatusCode})");

                    // レスポンスヘッダーからトークン・キー情報を取得
                    token = RadikoHttp.GetHeader(auth1Response, "X-Radiko-AuthToken");
                    var keyLengthText = RadikoHttp.GetHeader(auth1Response, "X-Radiko-KeyLength");
                    var keyOffsetText = RadikoHttp.GetHeader(auth1Response, "X-Radiko-KeyOffset");

                    if (string.IsNullOrEmpty(token))
                        throw new Exception("auth1: X-Radiko-AuthToken がレスポンスにありません");
                    if (string.IsNullOrEmpty(keyLengthText) || string.IsNullOrEmpty(keyOffsetText))
                        throw new Exception("auth1: KeyLength / KeyOffset がレスポンスにありません");

                    token = token.Trim();
                    keyLength = int.Parse(keyLengthText.Trim());
                    keyOffset = int.Parse(keyOffsetText.Trim());
                }
            }

            // --- Partial key生成 ---
            string partialKey = GeneratePartialKey(keyOffset, keyLength);

            // --- Step 2: auth2 ---
            string auth2Body;
            using (var auth2Request = new HttpRequestMessage(HttpMethod.Get, new Uri(AUTH2_URL)))
            {
                auth2Request.Headers.TryAppendWithoutValidation("X-Radiko-App", APP_NAME);
                auth2Request.Headers.TryAppendWithoutValidation("X-Radiko-App-Version", APP_VERSION);
                auth2Request.Headers.TryAppendWithoutValidation("X-Radiko-AuthToken", token);
                auth2Request.Headers.TryAppendWithoutValidation("X-Radiko-PartialKey", partialKey);
                auth2Request.Headers.TryAppendWithoutValidation("X-Radiko-User", userId);
                auth2Request.Headers.TryAppendWithoutValidation("X-Radiko-Device", DEVICE);

                using (var auth2Response = await _httpClient.SendRequestAsync(auth2Request))
                {
                    if (!auth2Response.IsSuccessStatusCode)
                        throw new Exception($"auth2 失敗 (HTTP {(int)auth2Response.StatusCode})。日本国外からはご利用いただけません。");

                    // レスポンスボディ: "JP13,東京都,tokyo Japan" 形式
                    var bodyBytes = (await auth2Response.Content.ReadAsBufferAsync()).ToArray();
                    auth2Body = Encoding.UTF8.GetString(bodyBytes, 0, bodyBytes.Length);
                }
            }

            ParseAreaInfo(auth2Body.Trim());
            if (string.IsNullOrEmpty(AreaId) || !AreaId.StartsWith("JP", StringComparison.Ordinal))
            {
                AreaId = null;
                throw new Exception("エリア判定に失敗しました (日本国外からはご利用いただけません)");
            }

            AuthToken = token;
        }

        private string GeneratePartialKey(int offset, int length)
        {
            var keyBytes = Encoding.ASCII.GetBytes(AUTH_KEY);
            if (offset < 0 || length <= 0 || offset + length > keyBytes.Length)
                throw new Exception($"auth1: 不正な KeyOffset/KeyLength ({offset}/{length})");

            return Convert.ToBase64String(keyBytes, offset, length);
        }

        private void ParseAreaInfo(string areaResponse)
        {
            // 例: "JP13,東京都,tokyo Japan"
            var parts = areaResponse.Split(',');
            if (parts.Length >= 1)
                AreaId = parts[0].Trim();
            if (parts.Length >= 2)
                AreaName = parts[1].Trim();
        }
    }
}
