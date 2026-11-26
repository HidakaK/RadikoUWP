using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Web.Http;

namespace RadikoUWP.Services
{
    /// <summary>
    /// radiko の HLS (セグメントは ID3 タグ付き AAC/ADTS の packed audio) を自前でダウンロード・パースし、
    /// MediaStreamSource として MediaPlayer に供給する。
    /// AdaptiveMediaSource は OS バージョンによって packed audio の HLS を再生できないため、こちらを優先して使う。
    ///
    /// ライブ: 最新位置の少し手前から再生し、プレイリストを定期的に再取得して追従する。
    /// タイムフリー: 番組の先頭 (または指定位置) から終わりまで再生する。シーク可能。
    /// </summary>
    public sealed class RadikoHlsMediaSource : IDisposable
    {
        private static readonly int[] SampleRates =
        {
            96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350
        };

        // ライブの最新位置から何セグメント手前から再生を始めるか (バッファ確保のため)
        private const int StartSegmentsFromLiveEdge = 3;
        private const int MaxConsecutiveErrors = 6;

        // ライブ: 一時停止中に溜め込む最大秒数。超えた分は古いものから捨てる (メモリ節約とライブとの遅延抑制)
        private const int MaxBufferedSecondsLive = 90;

        // タイムフリー: 先読みする最大秒数 (番組全体をメモリに読み込まないため)
        private const int MaxReadAheadSecondsTimefree = 60;

        private readonly HttpClient _httpClient;
        private readonly bool _isTimefree;
        private readonly Uri _livePlaylistUri;
        private readonly Func<DateTimeOffset, Uri> _timefreeUrlForSeek;
        private readonly DateTimeOffset _programStart;
        private readonly DateTimeOffset _programEnd;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private CancellationTokenSource _producerCts;

        private readonly ConcurrentQueue<AdtsFrame> _frames = new ConcurrentQueue<AdtsFrame>();
        private readonly SemaphoreSlim _frameSignal = new SemaphoreSlim(0);
        private readonly object _restartLock = new object();

        // ライブ用: 取得済みセグメントの管理
        private long _lastSequence = -1;
        private readonly HashSet<string> _seenSegments = new HashSet<string>();
        private readonly Queue<string> _seenOrder = new Queue<string>();

        private int _sampleRate;
        private int _channels;
        private int _generation;
        private TimeSpan _baseOffset;   // タイムフリー: 現在の再生開始位置 (番組先頭からの経過)
        private long _totalSamples;     // _baseOffset 以降に供給したサンプル数
        private volatile bool _endOfStream;
        private bool _started;
        private Exception _producerError;
        private bool _disposed;

        public MediaStreamSource MediaStreamSource { get; private set; }
        public bool IsTimefree => _isTimefree;
        public bool HasFailed => _producerError != null;

        /// <summary>再生中にストリームが継続不能になったとき (トークン期限切れ・通信断など) に発生。</summary>
        public event EventHandler<Exception> Failed;

        private RadikoHlsMediaSource(HttpClient httpClient, Uri livePlaylistUri)
        {
            _httpClient = httpClient;
            _livePlaylistUri = livePlaylistUri;
        }

        private RadikoHlsMediaSource(HttpClient httpClient, Func<DateTimeOffset, Uri> urlForSeek,
            DateTimeOffset programStart, DateTimeOffset programEnd)
        {
            _httpClient = httpClient;
            _isTimefree = true;
            _timefreeUrlForSeek = urlForSeek;
            _programStart = programStart;
            _programEnd = programEnd;
        }

        /// <summary>
        /// ライブ用。プレイリストと最初のセグメントを取得して MediaStreamSource を構築する。
        /// セグメントが AAC(ADTS) でない場合は NotSupportedException を投げる。
        /// </summary>
        public static async Task<RadikoHlsMediaSource> CreateAsync(HttpClient httpClient, Uri playlistUri)
        {
            var source = new RadikoHlsMediaSource(httpClient, playlistUri);
            try
            {
                await source.OpenLiveAsync();
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        /// <summary>
        /// タイムフリー用。urlForSeek はシーク位置 (日本時間の絶対時刻) からプレイリスト URL を作る関数。
        /// </summary>
        public static async Task<RadikoHlsMediaSource> CreateTimefreeAsync(HttpClient httpClient,
            Func<DateTimeOffset, Uri> urlForSeek, DateTimeOffset programStart, DateTimeOffset programEnd, TimeSpan startOffset)
        {
            var source = new RadikoHlsMediaSource(httpClient, urlForSeek, programStart, programEnd);
            try
            {
                await source.OpenTimefreeAsync(startOffset);
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        #region 初期化

        private async Task OpenLiveAsync()
        {
            var token = _cts.Token;
            var loaded = await LoadMediaPlaylistAsync(_livePlaylistUri, token);
            var media = loaded.Playlist;
            if (media.Segments.Count == 0)
                throw new Exception("プレイリストにセグメントがありません");

            // ライブ最新位置の少し手前から開始
            int start = Math.Max(0, media.Segments.Count - StartSegmentsFromLiveEdge);
            for (int i = 0; i < start; i++)
                MarkSeen(media.Segments[i]);

            var first = media.Segments[start];
            await LoadFirstSegmentAsync(first, token);
            MarkSeen(first);

            CreateMediaStreamSource();

            var remaining = media.Segments.GetRange(start + 1, media.Segments.Count - start - 1);
            var producerToken = NewProducerToken();
            int gen = _generation;
            var _ = Task.Run(() => LiveProducerLoopAsync(loaded.Uri, remaining, media.TargetDuration, gen, producerToken));
        }

        private async Task OpenTimefreeAsync(TimeSpan startOffset)
        {
            var token = _cts.Token;
            _baseOffset = ClampOffset(startOffset);
            var seekTime = _programStart + _baseOffset;

            var loaded = await LoadMediaPlaylistAsync(_timefreeUrlForSeek(seekTime), token);
            var media = loaded.Playlist;
            if (media.Segments.Count == 0)
                throw new Exception("プレイリストにセグメントがありません");

            var first = media.Segments[0];
            await LoadFirstSegmentAsync(first, token);

            CreateMediaStreamSource();

            var producerToken = NewProducerToken();
            int gen = _generation;
            var _ = Task.Run(() => TimefreeProducerLoopAsync(loaded, 1, first.Duration, seekTime, gen, producerToken));
        }

        private async Task LoadFirstSegmentAsync(HlsSegment first, CancellationToken token)
        {
            var data = await RadikoHttp.GetBytesAsync(_httpClient, first.Uri, token);
            var frames = ParseAdts(data);
            if (frames.Count == 0)
                throw new NotSupportedException("セグメントが AAC (ADTS) 形式ではありません");

            _sampleRate = frames[0].SampleRate;
            _channels = frames[0].Channels;
            EnqueueFrames(frames, _generation);
        }

        private void CreateMediaStreamSource()
        {
            var props = AudioEncodingProperties.CreateAacAdts((uint)_sampleRate, (uint)Math.Max(1, _channels), 48000);
            var mss = new MediaStreamSource(new AudioStreamDescriptor(props));
            mss.BufferTime = TimeSpan.FromSeconds(2);
            if (_isTimefree)
            {
                mss.CanSeek = true;
                mss.Duration = _programEnd - _programStart;
            }
            else
            {
                mss.CanSeek = false;
            }
            mss.Starting += OnStarting;
            mss.SampleRequested += OnSampleRequested;
            mss.Closed += OnClosed;
            MediaStreamSource = mss;
        }

        private CancellationToken NewProducerToken()
        {
            _producerCts?.Cancel();
            _producerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            return _producerCts.Token;
        }

        private TimeSpan ClampOffset(TimeSpan offset)
        {
            var duration = _programEnd - _programStart;
            if (offset < TimeSpan.Zero)
                return TimeSpan.Zero;
            if (offset > duration - TimeSpan.FromSeconds(1))
                return duration - TimeSpan.FromSeconds(1);
            return offset;
        }

        #endregion

        #region MediaStreamSource イベント

        private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            var request = args.Request;

            if (!_isTimefree)
            {
                request.SetActualStartPosition(TimeSpan.Zero);
                return;
            }

            var current = CurrentPosition;
            var requested = request.StartPosition;
            if (!_started || requested == null || Math.Abs((requested.Value - current).TotalSeconds) < 1)
            {
                // 初回開始、または一時停止からの再開
                _started = true;
                request.SetActualStartPosition(current);
                return;
            }

            // シーク: 現在の取得を中止し、指定位置からプレイリストを取り直す
            var target = ClampOffset(requested.Value);
            RestartTimefreeAt(target);
            request.SetActualStartPosition(target);
        }

        private TimeSpan CurrentPosition =>
            _sampleRate > 0 ? _baseOffset + TimeSpan.FromTicks(Interlocked.Read(ref _totalSamples) * 10000000L / _sampleRate) : _baseOffset;

        private void RestartTimefreeAt(TimeSpan offset)
        {
            CancellationToken token;
            int gen;
            lock (_restartLock)
            {
                gen = ++_generation;
                token = NewProducerToken();

                AdtsFrame dropped;
                while (_frames.TryDequeue(out dropped))
                {
                }
                _baseOffset = offset;
                Interlocked.Exchange(ref _totalSamples, 0);
                _endOfStream = false;
                _producerError = null;
            }

            var seekTime = _programStart + offset;
            var __ = Task.Run(() => TimefreeProducerLoopAsync(null, 0, 0, seekTime, gen, token));
        }

        private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var request = args.Request;
            var deferral = request.GetDeferral();
            try
            {
                AdtsFrame frame;
                while (true)
                {
                    if (_frames.TryDequeue(out frame))
                    {
                        if (frame.Generation == _generation)
                            break;
                        continue; // シーク前の古いフレーム
                    }

                    if (_producerError != null || _endOfStream || _cts.IsCancellationRequested)
                    {
                        // Sample=null でストリーム終端を通知
                        request.Sample = null;
                        return;
                    }
                    await _frameSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                }

                long samples = Interlocked.Add(ref _totalSamples, frame.SampleCount) - frame.SampleCount;
                var timestamp = _baseOffset + TimeSpan.FromTicks(samples * 10000000L / _sampleRate);
                var duration = TimeSpan.FromTicks(frame.SampleCount * 10000000L / _sampleRate);

                var sample = MediaStreamSample.CreateFromBuffer(frame.Data.AsBuffer(), timestamp);
                sample.Duration = duration;
                sample.KeyFrame = true;
                request.Sample = sample;
            }
            catch (OperationCanceledException)
            {
                request.Sample = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SampleRequested error: {ex}");
                request.Sample = null;
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args)
        {
            Dispose();
        }

        #endregion

        #region ダウンロード (プロデューサー)

        private async Task LiveProducerLoopAsync(Uri mediaPlaylistUri, List<HlsSegment> pending, double targetDuration,
            int gen, CancellationToken token)
        {
            int errorCount = 0;
            var queue = new Queue<HlsSegment>(pending);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 未取得のセグメントをダウンロード
                    while (queue.Count > 0 && !token.IsCancellationRequested)
                    {
                        var seg = queue.Peek();
                        var data = await RadikoHttp.GetBytesAsync(_httpClient, seg.Uri, token);
                        queue.Dequeue();
                        MarkSeen(seg);
                        EnqueueFrames(ParseAdts(data), gen);
                        errorCount = 0;
                    }

                    // プレイリストを再取得して新しいセグメントを探す
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1.0, targetDuration / 2)), token);

                    var text = await RadikoHttp.GetStringAsync(_httpClient, mediaPlaylistUri, token);
                    var media = ParseMediaPlaylist(text, mediaPlaylistUri);
                    if (media.TargetDuration > 0)
                        targetDuration = media.TargetDuration;

                    foreach (var seg in media.Segments)
                    {
                        if (!IsSeen(seg))
                            queue.Enqueue(seg);
                    }
                    errorCount = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!await HandleProducerErrorAsync(ex, ++errorCount, gen, token))
                        break;
                }
            }
        }

        /// <summary>
        /// タイムフリー: seekTime から番組終了まで順にセグメントを取得する。
        /// プレイリストが途中までしか返されない場合 (分割配信) は、取得済みの位置から取り直す。
        /// </summary>
        private async Task TimefreeProducerLoopAsync(LoadedPlaylist loaded, int nextIndex, double coveredSeconds,
            DateTimeOffset seekTime, int gen, CancellationToken token)
        {
            int errorCount = 0;
            int noProgressCount = 0;
            var seen = new HashSet<string>();
            if (loaded != null)
            {
                for (int i = 0; i < nextIndex && i < loaded.Playlist.Segments.Count; i++)
                    seen.Add(loaded.Playlist.Segments[i].Uri.AbsoluteUri);
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (loaded == null)
                    {
                        loaded = await LoadMediaPlaylistAsync(_timefreeUrlForSeek(seekTime), token);
                        coveredSeconds = 0;
                        noProgressCount = 0;
                        seen.Clear();
                        if (loaded.Playlist.Segments.Count == 0)
                        {
                            FinishStream(gen);
                            break;
                        }
                    }

                    bool progressed = false;
                    foreach (var seg in loaded.Playlist.Segments)
                    {
                        if (token.IsCancellationRequested)
                            break;
                        if (seen.Contains(seg.Uri.AbsoluteUri))
                            continue;

                        await WaitForReadAheadSpaceAsync(gen, token);
                        var data = await RadikoHttp.GetBytesAsync(_httpClient, seg.Uri, token);
                        seen.Add(seg.Uri.AbsoluteUri);
                        EnqueueFrames(ParseAdts(data), gen);
                        coveredSeconds += seg.Duration;
                        progressed = true;
                        errorCount = 0;
                    }

                    var coveredUntil = seekTime + TimeSpan.FromSeconds(coveredSeconds);
                    if (coveredUntil >= _programEnd - TimeSpan.FromSeconds(1))
                    {
                        FinishStream(gen);
                        break;
                    }

                    if (!progressed)
                        noProgressCount++;

                    if (loaded.Playlist.EndList || noProgressCount >= 3)
                    {
                        if (coveredSeconds <= 0)
                        {
                            // 進めないので終了 (無限ループ防止)
                            FinishStream(gen);
                            break;
                        }
                        // 続きの範囲を取得
                        seekTime = coveredUntil;
                        loaded = null;
                        continue;
                    }

                    // 終端が無いプレイリストは再取得して続きを待つ
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1.0, loaded.Playlist.TargetDuration / 2)), token);
                    var text = await RadikoHttp.GetStringAsync(_httpClient, loaded.Uri, token);
                    loaded = new LoadedPlaylist { Uri = loaded.Uri, Playlist = ParseMediaPlaylist(text, loaded.Uri) };
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!await HandleProducerErrorAsync(ex, ++errorCount, gen, token))
                        break;
                }
            }
        }

        private async Task WaitForReadAheadSpaceAsync(int gen, CancellationToken token)
        {
            int maxFrames = MaxReadAheadSecondsTimefree * Math.Max(1, _sampleRate) / 1024;
            while (_frames.Count > maxFrames && gen == _generation)
                await Task.Delay(500, token);
        }

        /// <summary>エラー時の処理。続行する場合 true。</summary>
        private async Task<bool> HandleProducerErrorAsync(Exception ex, int errorCount, int gen, CancellationToken token)
        {
            Debug.WriteLine($"HLS producer error ({errorCount}): {ex.Message}");

            var httpEx = ex as RadikoHttpException;
            bool fatal = errorCount >= MaxConsecutiveErrors
                         || (httpEx != null && ((int)httpEx.StatusCode == 401 || (int)httpEx.StatusCode == 403));
            if (fatal)
            {
                if (gen == _generation)
                {
                    _producerError = ex;
                    _frameSignal.Release();
                    Failed?.Invoke(this, ex);
                }
                return false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private void FinishStream(int gen)
        {
            if (gen != _generation)
                return;
            _endOfStream = true;
            _frameSignal.Release();
        }

        private void EnqueueFrames(List<AdtsFrame> frames, int gen)
        {
            if (gen != _generation)
                return;

            foreach (var f in frames)
            {
                // 途中でサンプルレートが変わるフレームは破棄 (デコーダー設定と合わないため)
                if (_sampleRate != 0 && f.SampleRate != _sampleRate)
                    continue;
                f.Generation = gen;
                _frames.Enqueue(f);
                _frameSignal.Release();
            }

            if (!_isTimefree && _sampleRate > 0)
            {
                int maxFrames = MaxBufferedSecondsLive * _sampleRate / 1024;
                AdtsFrame dropped;
                while (_frames.Count > maxFrames && _frames.TryDequeue(out dropped))
                {
                    // セマフォのカウントは余るが、消費側は空なら再度待つので問題ない
                }
            }
        }

        private bool IsSeen(HlsSegment seg)
        {
            if (seg.Sequence >= 0 && _lastSequence >= 0)
                return seg.Sequence <= _lastSequence;
            return _seenSegments.Contains(seg.Uri.AbsoluteUri);
        }

        private void MarkSeen(HlsSegment seg)
        {
            if (seg.Sequence > _lastSequence)
                _lastSequence = seg.Sequence;

            var key = seg.Uri.AbsoluteUri;
            if (_seenSegments.Add(key))
            {
                _seenOrder.Enqueue(key);
                while (_seenOrder.Count > 200)
                    _seenSegments.Remove(_seenOrder.Dequeue());
            }
        }

        #endregion

        #region HLS playlist parsing

        private sealed class HlsSegment
        {
            public Uri Uri;
            public long Sequence;
            public double Duration;
        }

        private sealed class MediaPlaylist
        {
            public double TargetDuration = 5;
            public bool EndList;
            public List<HlsSegment> Segments = new List<HlsSegment>();
        }

        private sealed class LoadedPlaylist
        {
            public Uri Uri;
            public MediaPlaylist Playlist;
        }

        /// <summary>マスタープレイリストなら最初のバリアントをたどってメディアプレイリストを取得する。</summary>
        private async Task<LoadedPlaylist> LoadMediaPlaylistAsync(Uri uri, CancellationToken token)
        {
            var text = await RadikoHttp.GetStringAsync(_httpClient, uri, token);
            var variant = FindVariantUri(text, uri);
            if (variant != null)
            {
                uri = variant;
                text = await RadikoHttp.GetStringAsync(_httpClient, uri, token);
            }
            return new LoadedPlaylist { Uri = uri, Playlist = ParseMediaPlaylist(text, uri) };
        }

        private static Uri FindVariantUri(string playlist, Uri baseUri)
        {
            var lines = SplitLines(playlist);
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        if (lines[j].Length > 0 && !lines[j].StartsWith("#", StringComparison.Ordinal))
                            return new Uri(baseUri, lines[j]);
                    }
                }
            }
            return null;
        }

        private static MediaPlaylist ParseMediaPlaylist(string playlist, Uri baseUri)
        {
            var result = new MediaPlaylist();
            long sequence = -1;
            bool hasSequence = false;
            bool expectUri = false;
            double segDuration = 0;

            foreach (var line in SplitLines(playlist))
            {
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                {
                    double d;
                    if (TryParseDouble(line.Substring(22), out d) && d > 0)
                        result.TargetDuration = d;
                }
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                {
                    long s;
                    if (long.TryParse(line.Substring(22).Trim(), out s))
                    {
                        sequence = s;
                        hasSequence = true;
                    }
                }
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
                {
                    result.EndList = true;
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Substring(8);
                    int comma = value.IndexOf(',');
                    if (comma >= 0)
                        value = value.Substring(0, comma);
                    if (!TryParseDouble(value, out segDuration))
                        segDuration = 0;
                    expectUri = true;
                }
                else if (!line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (expectUri)
                    {
                        result.Segments.Add(new HlsSegment
                        {
                            Uri = new Uri(baseUri, line),
                            Sequence = hasSequence ? sequence : -1,
                            Duration = segDuration > 0 ? segDuration : result.TargetDuration
                        });
                        if (hasSequence)
                            sequence++;
                    }
                    expectUri = false;
                }
            }

            return result;
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static List<string> SplitLines(string text)
        {
            var list = new List<string>();
            foreach (var raw in text.Split('\n'))
                list.Add(raw.Trim());
            return list;
        }

        #endregion

        #region ADTS parsing

        private sealed class AdtsFrame
        {
            public byte[] Data;
            public int SampleRate;
            public int Channels;
            public int SampleCount;
            public int Generation;
        }

        private static List<AdtsFrame> ParseAdts(byte[] data)
        {
            var frames = new List<AdtsFrame>();
            int pos = 0;
            int len = data.Length;

            // MPEG-2 TS (0x47 同期バイトが 188 バイト間隔) は対象外 → AdaptiveMediaSource にフォールバック
            if (len >= 376 && data[0] == 0x47 && data[188] == 0x47)
                return frames;

            while (pos + 7 <= len)
            {
                // ID3v2 タグ (radiko のセグメント先頭にはタイムスタンプ用 ID3 が付く)
                if (pos + 10 <= len && data[pos] == (byte)'I' && data[pos + 1] == (byte)'D' && data[pos + 2] == (byte)'3')
                {
                    int size = ((data[pos + 6] & 0x7F) << 21) | ((data[pos + 7] & 0x7F) << 14)
                               | ((data[pos + 8] & 0x7F) << 7) | (data[pos + 9] & 0x7F);
                    bool hasFooter = (data[pos + 5] & 0x10) != 0;
                    pos += 10 + size + (hasFooter ? 10 : 0);
                    continue;
                }

                // ADTS 同期ワード 0xFFF, layer == 0
                if (data[pos] == 0xFF && (data[pos + 1] & 0xF6) == 0xF0)
                {
                    int sfIndex = (data[pos + 2] >> 2) & 0x0F;
                    int channels = ((data[pos + 2] & 0x01) << 2) | ((data[pos + 3] >> 6) & 0x03);
                    int frameLength = ((data[pos + 3] & 0x03) << 11) | (data[pos + 4] << 3) | ((data[pos + 5] >> 5) & 0x07);
                    int headerLength = (data[pos + 1] & 0x01) != 0 ? 7 : 9;
                    int rawBlocks = (data[pos + 6] & 0x03) + 1;

                    if (sfIndex < SampleRates.Length && frameLength >= headerLength && pos + frameLength <= len)
                    {
                        var frame = new byte[frameLength];
                        Buffer.BlockCopy(data, pos, frame, 0, frameLength);
                        frames.Add(new AdtsFrame
                        {
                            Data = frame,
                            SampleRate = SampleRates[sfIndex],
                            Channels = channels == 0 ? 2 : channels,
                            SampleCount = 1024 * rawBlocks
                        });
                        pos += frameLength;
                        continue;
                    }

                    if (sfIndex < SampleRates.Length && frameLength >= headerLength && pos + frameLength > len)
                        break; // 末尾の不完全フレーム
                }

                // 同期が取れない場合は 1 バイト進めて再同期
                pos++;
            }

            return frames;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            _frameSignal.Release();

            if (MediaStreamSource != null)
            {
                MediaStreamSource.Starting -= OnStarting;
                MediaStreamSource.SampleRequested -= OnSampleRequested;
                MediaStreamSource.Closed -= OnClosed;
            }
        }
    }
}
