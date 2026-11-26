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
    /// radiko のライブ HLS (セグメントは ID3 タグ付き AAC/ADTS の packed audio) を
    /// 自前でダウンロード・パースし、MediaStreamSource として MediaPlayer に供給する。
    /// AdaptiveMediaSource は OS バージョンによって packed audio の HLS を再生できないため、こちらを優先して使う。
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

        private readonly HttpClient _httpClient;
        private readonly Uri _playlistUri;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly ConcurrentQueue<AdtsFrame> _frames = new ConcurrentQueue<AdtsFrame>();
        private readonly SemaphoreSlim _frameSignal = new SemaphoreSlim(0);

        private Uri _mediaPlaylistUri;
        private long _lastSequence = -1;
        private readonly HashSet<string> _seenSegments = new HashSet<string>();
        private readonly Queue<string> _seenOrder = new Queue<string>();

        private int _sampleRate;
        private int _channels;
        private long _totalSamples;
        private Exception _producerError;
        private Task _producerTask;
        private bool _disposed;

        public MediaStreamSource MediaStreamSource { get; private set; }

        /// <summary>再生中にストリームが継続不能になったとき (トークン期限切れ・通信断など) に発生。</summary>
        public event EventHandler<Exception> Failed;

        private RadikoHlsMediaSource(HttpClient httpClient, Uri playlistUri)
        {
            _httpClient = httpClient;
            _playlistUri = playlistUri;
        }

        /// <summary>
        /// プレイリストと最初のセグメントを取得して MediaStreamSource を構築する。
        /// セグメントが AAC(ADTS) でない場合は NotSupportedException を投げる。
        /// </summary>
        public static async Task<RadikoHlsMediaSource> CreateAsync(HttpClient httpClient, Uri playlistUri)
        {
            var source = new RadikoHlsMediaSource(httpClient, playlistUri);
            try
            {
                await source.OpenAsync();
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        private async Task OpenAsync()
        {
            var token = _cts.Token;

            // マスタープレイリスト → メディアプレイリストを解決
            var playlist = await RadikoHttp.GetStringAsync(_httpClient, _playlistUri, token);
            _mediaPlaylistUri = _playlistUri;
            var variant = FindVariantUri(playlist, _playlistUri);
            if (variant != null)
            {
                _mediaPlaylistUri = variant;
                playlist = await RadikoHttp.GetStringAsync(_httpClient, _mediaPlaylistUri, token);
            }

            var media = ParseMediaPlaylist(playlist, _mediaPlaylistUri);
            if (media.Segments.Count == 0)
                throw new Exception("プレイリストにセグメントがありません");

            // ライブ最新位置の少し手前から開始
            int start = Math.Max(0, media.Segments.Count - StartSegmentsFromLiveEdge);
            for (int i = 0; i < start; i++)
                MarkSeen(media.Segments[i]);

            // 最初のセグメントを取得してオーディオ形式を判定
            var first = media.Segments[start];
            var firstData = await RadikoHttp.GetBytesAsync(_httpClient, first.Uri, token);
            MarkSeen(first);

            var firstFrames = ParseAdts(firstData);
            if (firstFrames.Count == 0)
                throw new NotSupportedException("セグメントが AAC (ADTS) 形式ではありません");

            _sampleRate = firstFrames[0].SampleRate;
            _channels = firstFrames[0].Channels;
            EnqueueFrames(firstFrames);

            var props = AudioEncodingProperties.CreateAacAdts((uint)_sampleRate, (uint)Math.Max(1, _channels), 48000);
            var descriptor = new AudioStreamDescriptor(props);

            var mss = new MediaStreamSource(descriptor);
            mss.CanSeek = false;
            mss.BufferTime = TimeSpan.FromSeconds(2);
            mss.Starting += OnStarting;
            mss.SampleRequested += OnSampleRequested;
            mss.Closed += OnClosed;
            MediaStreamSource = mss;

            // 残りのセグメントの取得とプレイリスト更新はバックグラウンドで継続
            var remaining = media.Segments.GetRange(start + 1, media.Segments.Count - start - 1);
            _producerTask = Task.Run(() => ProducerLoopAsync(remaining, media.TargetDuration, token));
        }

        private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            args.Request.SetActualStartPosition(TimeSpan.Zero);
        }

        private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var request = args.Request;
            var deferral = request.GetDeferral();
            try
            {
                AdtsFrame frame;
                while (!_frames.TryDequeue(out frame))
                {
                    if (_producerError != null || _cts.IsCancellationRequested)
                    {
                        // Sample=null でストリーム終端を通知
                        request.Sample = null;
                        return;
                    }
                    await _frameSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                }

                var timestamp = TimeSpan.FromTicks(_totalSamples * 10000000L / _sampleRate);
                var duration = TimeSpan.FromTicks(frame.SampleCount * 10000000L / _sampleRate);
                _totalSamples += frame.SampleCount;

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

        private async Task ProducerLoopAsync(List<HlsSegment> pending, double targetDuration, CancellationToken token)
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
                        EnqueueFrames(ParseAdts(data));
                        errorCount = 0;
                    }

                    // プレイリストを再取得して新しいセグメントを探す
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1.0, targetDuration / 2)), token);

                    var text = await RadikoHttp.GetStringAsync(_httpClient, _mediaPlaylistUri, token);
                    var media = ParseMediaPlaylist(text, _mediaPlaylistUri);
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
                    errorCount++;
                    Debug.WriteLine($"HLS producer error ({errorCount}): {ex.Message}");

                    var httpEx = ex as RadikoHttpException;
                    bool fatal = errorCount >= MaxConsecutiveErrors
                                 || (httpEx != null && ((int)httpEx.StatusCode == 401 || (int)httpEx.StatusCode == 403));
                    if (fatal)
                    {
                        _producerError = ex;
                        _frameSignal.Release();
                        Failed?.Invoke(this, ex);
                        break;
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private void EnqueueFrames(List<AdtsFrame> frames)
        {
            foreach (var f in frames)
            {
                // 途中でサンプルレートが変わるフレームは破棄 (デコーダー設定と合わないため)
                if (_sampleRate != 0 && f.SampleRate != _sampleRate)
                    continue;
                _frames.Enqueue(f);
                _frameSignal.Release();
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

        #region HLS playlist parsing

        private sealed class HlsSegment
        {
            public Uri Uri;
            public long Sequence;
        }

        private sealed class MediaPlaylist
        {
            public double TargetDuration = 5;
            public List<HlsSegment> Segments = new List<HlsSegment>();
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

            foreach (var line in SplitLines(playlist))
            {
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                {
                    double d;
                    if (double.TryParse(line.Substring(22).Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out d) && d > 0)
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
                else if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    expectUri = true;
                }
                else if (!line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (expectUri)
                    {
                        result.Segments.Add(new HlsSegment
                        {
                            Uri = new Uri(baseUri, line),
                            Sequence = hasSequence ? sequence : -1
                        });
                        if (hasSequence)
                            sequence++;
                    }
                    expectUri = false;
                }
            }

            return result;
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
