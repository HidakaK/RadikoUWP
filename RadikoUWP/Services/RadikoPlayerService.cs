using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Web.Http;

namespace RadikoUWP.Services
{
    public class RadikoPlayerService : IDisposable
    {
        private readonly MediaPlayer _mediaPlayer;
        private readonly RadikoStationService _stationService;
        private HttpClient _httpClient;
        private string _authToken;
        private RadikoHlsMediaSource _hlsSource;
        private int _playSession;

        public MediaPlayer MediaPlayer => _mediaPlayer;
        public bool IsPlaying { get; private set; }
        public string CurrentStationId { get; private set; }

        /// <summary>再生中にエラーで停止したときに発生 (UI スレッド以外から呼ばれる)。</summary>
        public event EventHandler<string> PlaybackFailed;

        public RadikoPlayerService(RadikoStationService stationService)
        {
            _stationService = stationService;

            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.AudioCategory = MediaPlayerAudioCategory.Media;
            _mediaPlayer.AutoPlay = false;
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.MediaFailed += OnMediaFailed;
        }

        public void SetAuthToken(string authToken)
        {
            _authToken = authToken;

            // トークンが変わったらクライアントを作り直す (AdaptiveMediaSource は既定ヘッダーを使うため)
            _httpClient?.Dispose();
            _httpClient = RadikoHttp.CreateClient();
            _httpClient.DefaultRequestHeaders.TryAppendWithoutValidation("X-Radiko-AuthToken", _authToken);
        }

        public async Task PlayStationAsync(string stationId, string stationName)
        {
            if (string.IsNullOrEmpty(_authToken) || _httpClient == null)
                throw new InvalidOperationException("AuthToken が設定されていません。先に認証を行ってください。");

            // 再生中の場合は停止してから開始
            Stop();
            int session = ++_playSession;

            var candidates = await _stationService.GetLiveStreamUrlsAsync(stationId);
            var errors = new List<string>();
            MediaSource source = null;

            foreach (var url in candidates)
            {
                if (session != _playSession)
                    return; // 途中で停止/別局が選ばれた

                // 1) 自前 HLS (AAC/ADTS) パーサー
                try
                {
                    var hls = await RadikoHlsMediaSource.CreateAsync(_httpClient, url);
                    if (session != _playSession)
                    {
                        hls.Dispose();
                        return;
                    }
                    hls.Failed += OnHlsFailed;
                    _hlsSource = hls;
                    source = MediaSource.CreateFromMediaStreamSource(hls.MediaStreamSource);
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HLS(MSS) 失敗 {url}: {ex.Message}");
                    errors.Add(ex.Message);
                }

                // 2) OS 標準の AdaptiveMediaSource
                try
                {
                    var result = await AdaptiveMediaSource.CreateFromUriAsync(url, _httpClient);
                    if (result.Status == AdaptiveMediaSourceCreationStatus.Success)
                    {
                        source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
                        break;
                    }
                    errors.Add($"AdaptiveMediaSource: {result.Status}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"AdaptiveMediaSource 失敗 {url}: {ex.Message}");
                    errors.Add(ex.Message);
                }
            }

            if (session != _playSession)
            {
                source?.Dispose();
                return;
            }

            if (source == null)
                throw new Exception("ストリームを開けませんでした: " + string.Join(" / ", errors));

            var item = new MediaPlaybackItem(source);
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = string.IsNullOrEmpty(stationName) ? stationId : stationName;
            props.MusicProperties.Artist = "radiko";
            item.ApplyDisplayProperties(props);

            CurrentStationId = stationId;
            _mediaPlayer.Source = item;
            _mediaPlayer.Play();
            IsPlaying = true;
        }

        public void Stop()
        {
            _playSession++;

            if (_mediaPlayer.Source != null)
            {
                _mediaPlayer.Pause();
                var old = _mediaPlayer.Source;
                _mediaPlayer.Source = null;
                var oldItem = old as MediaPlaybackItem;
                if (oldItem != null)
                    oldItem.Source?.Dispose();
                else
                    (old as IDisposable)?.Dispose();
            }

            if (_hlsSource != null)
            {
                _hlsSource.Failed -= OnHlsFailed;
                _hlsSource.Dispose();
                _hlsSource = null;
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
            _httpClient?.Dispose();
        }

        private void OnHlsFailed(object sender, Exception ex)
        {
            if (sender != _hlsSource)
                return;
            IsPlaying = false;
            PlaybackFailed?.Invoke(this, "ストリームが途切れました: " + ex.Message);
        }

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            if (!IsPlaying)
                return;
            IsPlaying = false;
            PlaybackFailed?.Invoke(this, "ストリームが終了しました");
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            IsPlaying = false;
            PlaybackFailed?.Invoke(this, $"再生に失敗しました: {args.Error} {args.ErrorMessage} (0x{args.ExtendedErrorCode?.HResult:X8})");
        }
    }
}
