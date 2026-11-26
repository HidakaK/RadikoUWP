using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        private MediaPlaybackItem _currentItem;

        public MediaPlayer MediaPlayer => _mediaPlayer;

        /// <summary>ストリームを開いている (再生中・バッファリング中・一時停止中) かどうか。</summary>
        public bool IsPlaying { get; private set; }
        public string CurrentStationId { get; private set; }

        public MediaPlaybackState PlaybackState => _mediaPlayer.PlaybackSession.PlaybackState;

        private volatile bool _hasPlayed;

        /// <summary>一度再生が始まった後に一時停止された状態 (システムのメディアコントロールからの一時停止など)。</summary>
        public bool IsPaused => IsPlaying && _hasPlayed && PlaybackState == MediaPlaybackState.Paused;

        /// <summary>再生状態が変わったときに発生 (UI スレッド以外から呼ばれる)。</summary>
        public event EventHandler StateChanged;

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
            _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        }

        public void SetAuthToken(string authToken, string areaId)
        {
            _authToken = authToken;

            // トークンが変わったらクライアントを作り直す (AdaptiveMediaSource は既定ヘッダーを使うため)
            _httpClient?.Dispose();
            _httpClient = RadikoHttp.CreateClient();
            _httpClient.DefaultRequestHeaders.TryAppendWithoutValidation("X-Radiko-AuthToken", _authToken);
            if (!string.IsNullOrEmpty(areaId))
                _httpClient.DefaultRequestHeaders.TryAppendWithoutValidation("X-Radiko-AreaId", areaId);
        }

        /// <summary>タイムフリー再生中かどうか。</summary>
        public bool IsTimefree { get; private set; }

        /// <summary>タイムフリー: 番組先頭からの再生位置。</summary>
        public TimeSpan Position => _mediaPlayer.PlaybackSession.Position;

        /// <summary>タイムフリー: 番組の長さ。</summary>
        public TimeSpan Duration { get; private set; }

        /// <summary>タイムフリー: 指定位置へ移動する。</summary>
        public void Seek(TimeSpan position)
        {
            if (IsPlaying && IsTimefree && _mediaPlayer.PlaybackSession.CanSeek)
                _mediaPlayer.PlaybackSession.Position = position;
        }

        public void Pause()
        {
            if (IsPlaying)
                _mediaPlayer.Pause();
        }

        /// <summary>タイムフリー番組を最後まで再生したときに発生 (UI スレッド以外から呼ばれる)。</summary>
        public event EventHandler PlaybackEnded;

        /// <summary>
        /// タイムフリー番組を再生する。startOffset は番組先頭からの開始位置。
        /// </summary>
        public async Task PlayTimefreeAsync(string stationId, string stationName, Models.ProgramInfo program, TimeSpan startOffset)
        {
            if (string.IsNullOrEmpty(_authToken) || _httpClient == null)
                throw new InvalidOperationException("AuthToken が設定されていません。先に認証を行ってください。");

            Stop();
            _hasPlayed = false;
            int session = ++_playSession;

            var builders = await _stationService.GetTimefreeUrlBuildersAsync(stationId, program);
            var errors = new List<string>();
            RadikoHlsMediaSource hls = null;

            foreach (var build in builders)
            {
                if (session != _playSession)
                    return;
                try
                {
                    hls = await RadikoHlsMediaSource.CreateTimefreeAsync(_httpClient, build, program.Start, program.End, startOffset);
                    break;
                }
                catch (Exception ex)
                {
                    var httpEx = ex as RadikoHttpException;
                    if (httpEx != null && ((int)httpEx.StatusCode == 401 || (int)httpEx.StatusCode == 403))
                        errors.Add("配信が拒否されました (聴取時間の上限 (3 時間) に達したか、認証の期限切れの可能性があります)");
                    else
                        errors.Add(ex.Message);
                    Debug.WriteLine($"タイムフリー失敗: {ex.Message}");
                }
            }

            if (session != _playSession)
            {
                hls?.Dispose();
                return;
            }
            if (hls == null)
                throw new Exception(string.Join(" / ", errors.Distinct()));

            hls.Failed += OnHlsFailed;
            _hlsSource = hls;

            var item = new MediaPlaybackItem(MediaSource.CreateFromMediaStreamSource(hls.MediaStreamSource));
            _currentItem = item;
            UpdateDisplay(stationName, program.Title);

            IsTimefree = true;
            Duration = program.End - program.Start;
            CurrentStationId = stationId;
            _mediaPlayer.Source = item;
            _mediaPlayer.Play();
            IsPlaying = true;
            RaiseStateChanged();
        }

        public async Task PlayStationAsync(string stationId, string stationName, string programTitle = null)
        {
            if (string.IsNullOrEmpty(_authToken) || _httpClient == null)
                throw new InvalidOperationException("AuthToken が設定されていません。先に認証を行ってください。");

            // 再生中の場合は停止してから開始
            Stop();
            _hasPlayed = false;
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
            _currentItem = item;
            UpdateDisplay(string.IsNullOrEmpty(stationName) ? stationId : stationName, programTitle);

            IsTimefree = false;
            Duration = TimeSpan.Zero;
            CurrentStationId = stationId;
            _mediaPlayer.Source = item;
            _mediaPlayer.Play();
            IsPlaying = true;
            RaiseStateChanged();
        }

        /// <summary>一時停止中 (システムのメディアコントロールで一時停止された場合) の再開。</summary>
        public void Resume()
        {
            if (IsPlaying)
                _mediaPlayer.Play();
        }

        /// <summary>システムのメディアコントロール (ロック画面など) に表示する情報を更新する。</summary>
        public void UpdateDisplay(string stationName, string programTitle)
        {
            var item = _currentItem;
            if (item == null)
                return;

            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = string.IsNullOrEmpty(programTitle) ? stationName : programTitle;
            props.MusicProperties.Artist = string.IsNullOrEmpty(programTitle) ? "radiko" : stationName;
            props.MusicProperties.AlbumTitle = "radiko";
            item.ApplyDisplayProperties(props);
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

            _currentItem = null;
            bool changed = IsPlaying;
            IsPlaying = false;
            IsTimefree = false;
            Duration = TimeSpan.Zero;
            CurrentStationId = null;
            if (changed)
                RaiseStateChanged();
        }

        public void Dispose()
        {
            Stop();
            _mediaPlayer.MediaEnded -= OnMediaEnded;
            _mediaPlayer.MediaFailed -= OnMediaFailed;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            _mediaPlayer.Dispose();
            _httpClient?.Dispose();
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.PlaybackState == MediaPlaybackState.Playing)
                _hasPlayed = true;
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
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

            // タイムフリーは番組の終わりまで再生したら正常終了
            if (IsTimefree && _hlsSource != null && !_hlsSource.HasFailed)
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            else
                PlaybackFailed?.Invoke(this, "ストリームが終了しました");
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            IsPlaying = false;
            PlaybackFailed?.Invoke(this, $"再生に失敗しました: {args.Error} {args.ErrorMessage} (0x{args.ExtendedErrorCode?.HResult:X8})");
        }
    }
}
