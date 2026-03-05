using System;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Web.Http;

namespace RadikoUWP.Services
{
    public class RadikoPlayerService
    {
        private const string LIVE_STREAM_URL = "https://radiko.jp/v2/api/ts/playlist.m3u8?station_id={0}&l=15";

        private MediaPlayer _mediaPlayer;
        private HttpClient _httpClient;
        private string _authToken;

        public MediaPlayer MediaPlayer => _mediaPlayer;
        public bool IsPlaying { get; private set; }
        public string CurrentStationId { get; private set; }

        public RadikoPlayerService()
        {
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.AudioCategory = MediaPlayerAudioCategory.Media;
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.MediaFailed += OnMediaFailed;

            _httpClient = new HttpClient();
        }

        public void SetAuthToken(string authToken)
        {
            _authToken = authToken;

            // 認証トークンヘッダーを更新
            _httpClient.DefaultRequestHeaders.Remove("X-Radiko-AuthToken");
            _httpClient.DefaultRequestHeaders.TryAppendWithoutValidation("X-Radiko-AuthToken", _authToken);
        }

        public async Task PlayStationAsync(string stationId)
        {
            if (string.IsNullOrEmpty(_authToken))
                throw new InvalidOperationException("AuthToken が設定されていません。先に認証を行ってください。");

            // 再生中の場合は停止してから開始
            Stop();

            CurrentStationId = stationId;
            var streamUrl = string.Format(LIVE_STREAM_URL, stationId);

            // AdaptiveMediaSource で HLS ストリームを作成
            var result = await AdaptiveMediaSource.CreateFromUriAsync(
                new Uri(streamUrl),
                _httpClient);

            if (result.Status == AdaptiveMediaSourceCreationStatus.Success)
            {
                _mediaPlayer.Source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
                _mediaPlayer.Play();
                IsPlaying = true;
            }
            else
            {
                throw new Exception($"ストリームの作成に失敗しました: {result.Status}");
            }
        }

        public void Stop()
        {
            if (_mediaPlayer.Source != null)
            {
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
            }
            IsPlaying = false;
            CurrentStationId = null;
        }

        public void Dispose()
        {
            Stop();
            _mediaPlayer.MediaEnded -= OnMediaEnded;
            _mediaPlayer.MediaFailed -= OnMediaFailed;
            _mediaPlayer.Dispose();
            _httpClient.Dispose();
        }

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            IsPlaying = false;
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            IsPlaying = false;
        }
    }
}

