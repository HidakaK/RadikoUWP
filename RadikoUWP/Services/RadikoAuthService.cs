using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RadikoUWP.Services
{
    public class RadikoAuthService
    {
        // radiko認証キー (JavaScriptプレーヤーから)
        private const string AUTH_KEY = "bcd151073c03b352e1ef2fd66c32209da9ca0afa";

        private const string AUTH1_URL = "https://radiko.jp/v2/api/auth1";
        private const string AUTH2_URL = "https://radiko.jp/v2/api/auth2";

        private const string APP_NAME = "pc_html5";
        private const string APP_VERSION = "0.0.1";
        private const string DEVICE = "pc";

        private static readonly HttpClient _httpClient = new HttpClient();

        public string AuthToken { get; private set; }
        public string AreaId { get; private set; }
        public string AreaName { get; private set; }

        public bool IsAuthenticated => !string.IsNullOrEmpty(AuthToken) && !string.IsNullOrEmpty(AreaId);

        public async Task AuthenticateAsync()
        {
            var userId = Guid.NewGuid().ToString("N");

            // --- Step 1: auth1 ---
            var auth1Request = new HttpRequestMessage(HttpMethod.Get, AUTH1_URL);
            auth1Request.Headers.Add("X-Radiko-App", APP_NAME);
            auth1Request.Headers.Add("X-Radiko-App-Version", APP_VERSION);
            auth1Request.Headers.Add("X-Radiko-Device", DEVICE);
            auth1Request.Headers.Add("X-Radiko-User", userId);

            var auth1Response = await _httpClient.SendAsync(auth1Request);
            auth1Response.EnsureSuccessStatusCode();

            // レスポンスヘッダーからトークン・キー情報を取得
            if (!auth1Response.Headers.TryGetValues("X-Radiko-AuthToken", out var tokenValues))
                throw new Exception("auth1: X-Radiko-AuthToken not found in response headers");

            if (!auth1Response.Headers.TryGetValues("X-Radiko-KeyLength", out var keyLengthValues))
                throw new Exception("auth1: X-Radiko-KeyLength not found in response headers");

            if (!auth1Response.Headers.TryGetValues("X-Radiko-KeyOffset", out var keyOffsetValues))
                throw new Exception("auth1: X-Radiko-KeyOffset not found in response headers");

            AuthToken = string.Join("", tokenValues).Trim();
            int keyLength = int.Parse(string.Join("", keyLengthValues).Trim());
            int keyOffset = int.Parse(string.Join("", keyOffsetValues).Trim());

            // --- Partial key生成 ---
            string partialKey = GeneratePartialKey(keyOffset, keyLength);

            // --- Step 2: auth2 ---
            var auth2Request = new HttpRequestMessage(HttpMethod.Get, AUTH2_URL);
            auth2Request.Headers.Add("X-Radiko-AuthToken", AuthToken);
            auth2Request.Headers.Add("X-Radiko-PartialKey", partialKey);
            auth2Request.Headers.Add("X-Radiko-User", userId);
            auth2Request.Headers.Add("X-Radiko-Device", DEVICE);

            var auth2Response = await _httpClient.SendAsync(auth2Request);
            auth2Response.EnsureSuccessStatusCode();

            // レスポンスボディ: "JP13,東京都,tokyo" 形式
            var auth2Body = await auth2Response.Content.ReadAsStringAsync();
            ParseAreaInfo(auth2Body.Trim());
        }

        private string GeneratePartialKey(int offset, int length)
        {
            var keyBytes = Encoding.ASCII.GetBytes(AUTH_KEY);
            var partialBytes = new byte[length];
            Array.Copy(keyBytes, offset, partialBytes, 0, length);
            return Convert.ToBase64String(partialBytes);
        }

        private void ParseAreaInfo(string areaResponse)
        {
            // 例: "JP13,東京都,tokyo"
            var parts = areaResponse.Split(',');
            if (parts.Length >= 1)
                AreaId = parts[0].Trim();
            if (parts.Length >= 2)
                AreaName = parts[1].Trim();
        }
    }
}

