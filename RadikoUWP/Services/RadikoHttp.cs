using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace RadikoUWP.Services
{
    /// <summary>
    /// radiko 通信用の HttpClient を生成するヘルパー。
    /// UWP の HTTP スタックは既定でキャッシュを使うため、ライブのプレイリストが
    /// 更新されなかったり、古い認証レスポンスが返ったりする。キャッシュを無効化したクライアントを使う。
    /// </summary>
    internal static class RadikoHttp
    {
        public static HttpClient CreateClient()
        {
            var filter = new HttpBaseProtocolFilter();
            filter.AllowUI = false;
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
            filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;

            var client = new HttpClient(filter);
            client.DefaultRequestHeaders.TryAppendWithoutValidation("Cache-Control", "no-cache");
            client.DefaultRequestHeaders.TryAppendWithoutValidation("Pragma", "no-cache");
            return client;
        }

        public static string GetHeader(HttpResponseMessage response, string name)
        {
            foreach (KeyValuePair<string, string> header in response.Headers)
            {
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                    return header.Value;
            }
            return null;
        }

        public static async Task<byte[]> GetBytesAsync(HttpClient client, Uri uri, CancellationToken token)
        {
            using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseContentRead).AsTask(token))
            {
                if (!response.IsSuccessStatusCode)
                    throw new RadikoHttpException(response.StatusCode, uri);

                var buffer = await response.Content.ReadAsBufferAsync().AsTask(token);
                return buffer.ToArray();
            }
        }

        public static async Task<string> GetStringAsync(HttpClient client, Uri uri, CancellationToken token)
        {
            var bytes = await GetBytesAsync(client, uri, token);
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length).TrimStart('﻿');
        }
    }

    public class RadikoHttpException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public RadikoHttpException(HttpStatusCode statusCode, Uri uri)
            : base($"HTTP {(int)statusCode} ({statusCode}): {uri}")
        {
            StatusCode = statusCode;
        }
    }
}
