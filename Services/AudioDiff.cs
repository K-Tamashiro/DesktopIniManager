using DesktopIniManager.Controls;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DesktopIniManager.Services;

// Compare decoded amplitudes, independent of playback gain and display resolution.
internal sealed class AudioDifferences
{
    internal readonly List<(double Start, double End)>[] Channels;
    internal List<(double Start, double End)>[] TargetChannels;

    /// <summary>
    ///     チャンネルごとの差分区間リストを初期化します。
    /// </summary>
    /// <param name="channels">チャンネル数。</param>
    private AudioDifferences(int channels)
    {
        Channels = new List<(double, double)>[channels];
        for (var ch = 0; ch < channels; ch++) Channels[ch] = new List<(double Start, double End)>();
        TargetChannels = Channels;
    }

    /// <summary>
    ///     2 つの音声を同じサンプリングレートで順次デコードし、10 ミリ秒ごとの RMS を比較して、チャンネルごとの差分区間を検出します。
    ///     再生音量や表示解像度に依存せず、デコード後の振幅で比較します。
    /// </summary>
    /// <param name="source">比較元の音声ファイル。</param>
    /// <param name="target">比較先の音声ファイル。</param>
    /// <param name="cancellation">キャンセル トークン。</param>
    /// <returns>チャンネルごとの差分区間 (秒)。</returns>
    internal static AudioDifferences Load(string source, string target, CancellationToken cancellation)
    {
        using var a = !string.IsNullOrEmpty(source) && File.Exists(source) ? new AudioFileReader(source) : null;
        using var b = !string.IsNullOrEmpty(target) && File.Exists(target) ? new AudioFileReader(target) : null;
        var rate = Math.Max(a?.WaveFormat.SampleRate ?? 0, b?.WaveFormat.SampleRate ?? 0);
        var channels = Math.Max(a?.WaveFormat.Channels ?? 0, b?.WaveFormat.Channels ?? 0);
        var result = new AudioDifferences(channels);
        if (rate == 0) return result;
        // Decode sequentially in blocks. Resample only when input rates differ.
        ISampleProvider streamA = a == null ? null :
            a.WaveFormat.SampleRate == rate ? a : new WdlResamplingSampleProvider(a, rate);
        ISampleProvider streamB = b == null ? null :
            b.WaveFormat.SampleRate == rate ? b : new WdlResamplingSampleProvider(b, rate);
        var inputA = streamA == null ? null : new FrameStream(streamA);
        var inputB = streamB == null ? null : new FrameStream(streamB);
        const double absoluteTolerance = 0.012;
        const double relativeTolerance = 0.15;
        var windowFrames = Math.Max(1, (int)Math.Round(rate * 0.01));
        var starts = new long[channels];
        Array.Fill(starts, -1L);
        var energyA = new double[channels];
        var energyB = new double[channels];
        var countA = new int[channels];
        var countB = new int[channels];
        long frame = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Array.Clear(energyA);
            Array.Clear(energyB);
            Array.Clear(countA);
            Array.Clear(countB);
            var first = frame;
            for (var i = 0; i < windowFrames; i++, frame++)
            {
                var hasA = inputA?.NextFrame() == true;
                var hasB = inputB?.NextFrame() == true;
                if (!hasA && !hasB) break;
                for (var ch = 0; ch < channels; ch++)
                {
                    if (hasA && ch < inputA.Channels)
                    {
                        double value = inputA.Sample(ch);
                        energyA[ch] += value * value;
                        countA[ch]++;
                    }

                    if (hasB && ch < inputB.Channels)
                    {
                        double value = inputB.Sample(ch);
                        energyB[ch] += value * value;
                        countB[ch]++;
                    }
                }
            }

            if (frame == first) break;
            for (var ch = 0; ch < channels; ch++)
            {
                var rmsA = countA[ch] == 0 ? 0 : Math.Sqrt(energyA[ch] / countA[ch]);
                var rmsB = countB[ch] == 0 ? 0 : Math.Sqrt(energyB[ch] / countB[ch]);
                var different = countA[ch] != countB[ch] ||
                                Math.Abs(rmsA - rmsB) > Math.Max(absoluteTolerance,
                                    Math.Max(rmsA, rmsB) * relativeTolerance);
                if (different && starts[ch] < 0) starts[ch] = first;
                if (!different && starts[ch] >= 0)
                {
                    result.Channels[ch].Add(((double)starts[ch] / rate, (double)first / rate));
                    starts[ch] = -1;
                }
            }
        }

        for (var ch = 0; ch < channels; ch++)
            if (starts[ch] >= 0)
                result.Channels[ch].Add(((double)starts[ch] / rate, (double)frame / rate));
        return result;
    }

    // Buffered, sequential sample reader; no per-frame allocation or copying.
    private sealed class FrameStream
    {
        private readonly float[] _buffer;
        private readonly ISampleProvider _source;
        private bool _ended;
        private int _frameOffset;
        private int _offset, _available;

        /// <summary>
        ///     サンプル プロバイダーを、バッファー付きでフレーム単位に読み取るストリームを作成します。
        /// </summary>
        /// <param name="source">読み取り元のサンプル プロバイダー。</param>
        internal FrameStream(ISampleProvider source)
        {
            _source = source;
            Channels = source.WaveFormat.Channels;
            _buffer = new float[16384 * Channels];
        }

        internal int Channels { get; }

        /// <summary>
        ///     次のフレームへ進みます。必要に応じてバッファーを補充します。
        /// </summary>
        /// <returns>フレームを読み取れた場合は true。末尾に達した場合は false。</returns>
        internal bool NextFrame()
        {
            if (_ended) return false;
            if (_offset >= _available)
            {
                _available = _source.Read(_buffer, 0, _buffer.Length);
                _offset = 0;
                if (_available == 0)
                {
                    _ended = true;
                    return false;
                }

                if (_available % Channels != 0)
                    throw new InvalidDataException("Incomplete audio frame.");
            }

            _frameOffset = _offset;
            _offset += Channels;
            return true;
        }

        /// <summary>
        ///     現在のフレームの指定チャンネルのサンプル値を取得します。
        /// </summary>
        /// <param name="channel">チャンネル番号。</param>
        /// <returns>サンプル値。</returns>
        internal float Sample(int channel)
        {
            return _buffer[_frameOffset + channel];
        }
    }
}

// Bounded peak envelope: decoding is streamed, never retained as a full PCM file.
internal sealed class AudioEnvelope
{
    internal int? BitDepth;
    internal long? BitRate;
    internal int Channels;
    internal WaveformData Data;
    internal double Duration;
    internal bool FloatingPoint;
    internal int SampleRate;

    /// <summary>
    ///     音声ファイルのビット深度、ビットレート、浮動小数点形式かどうかを読み取ります。MP3 はフレームから平均ビットレートを求めます。
    /// </summary>
    /// <param name="path">音声ファイルのパス。</param>
    /// <param name="cancellation">キャンセル トークン。</param>
    private void ReadFormat(string path, CancellationToken cancellation)
    {
        if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            using var mp3 = new Mp3FileReader(path);
            double seconds = 0, weightedBits = 0;
            Mp3Frame frame;
            while ((frame = mp3.ReadNextFrame()) != null)
            {
                cancellation.ThrowIfCancellationRequested();
                var length = (double)frame.SampleCount / frame.SampleRate;
                seconds += length;
                weightedBits += frame.BitRate * length;
            }

            if (seconds > 0) BitRate = (long)Math.Round(weightedBits / seconds);
            // Lossy MP3 has no source PCM bit depth; do not report decoder float precision.
            return;
        }

        using var wav = new WaveFileReader(path);
        var format = wav.WaveFormat;
        FloatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                        (format is WaveFormatExtensible extensible &&
                         extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        if (format.Encoding == WaveFormatEncoding.Pcm || FloatingPoint ||
            (format is WaveFormatExtensible pcm && pcm.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")))
            BitDepth = format.BitsPerSample;
        BitRate = (long)format.AverageBytesPerSecond * 8;
    }

    /// <summary>
    ///     音声ファイルの波形データと形式情報を読み込みます。対応するのはモノラルとステレオだけです。
    /// </summary>
    /// <param name="path">音声ファイルのパス。</param>
    /// <param name="cancellation">キャンセル トークン。</param>
    /// <returns>読み込んだ波形情報。ファイルがない場合は null。</returns>
    internal static AudioEnvelope Load(string path, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var data = WaveformData.Load(path, cancellation);
        if (data.Channels > 2) throw new InvalidDataException("Audio Diff supports mono and stereo audio.");
        var result = new AudioEnvelope
        {
            Data = data,
            Channels = data.Channels,
            SampleRate = data.SampleRate,
            Duration = data.Duration
        };
        result.ReadFormat(path, cancellation);
        return result;
    }
}

// Both inputs feed one output clock; shorter inputs contribute silence.
internal sealed class AudioRepeatProvider : ISampleProvider
{
    private readonly Lock _gate = new();
    private readonly Action<double> _seek;
    private readonly ISampleProvider _source;
    private long _frame, _start, _end;

    /// <summary>
    ///     区間リピート再生を行うサンプル プロバイダーを作成します。
    /// </summary>
    /// <param name="source">読み取り元のサンプル プロバイダー。</param>
    /// <param name="seek">再生位置を戻すときに入力側をシークする処理 (秒)。</param>
    internal AudioRepeatProvider(ISampleProvider source, Action<double> seek)
    {
        _source = source;
        _seek = seek;
    }

    internal bool Enabled { get; private set; }

    internal double Position
    {
        get
        {
            lock (_gate)
            {
                return (double)_frame / WaveFormat.SampleRate;
            }
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>
    ///     サンプルを読み取ります。リピート区間が有効な場合は区間の終端で開始位置に戻り、短い入力の不足分は無音で埋めます。
    /// </summary>
    /// <param name="buffer">書き込み先のバッファー。</param>
    /// <param name="offset">書き込み開始位置。</param>
    /// <param name="count">読み取るサンプル数。</param>
    /// <returns>書き込んだサンプル数。</returns>
    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            int written = 0, channels = WaveFormat.Channels;
            while (written < count)
            {
                if (Enabled && (_frame < _start || _frame >= _end))
                {
                    _frame = _start;
                    _seek((double)_frame / WaveFormat.SampleRate);
                }

                var requested = count - written;
                if (Enabled) requested = (int)Math.Min(requested, (_end - _frame) * channels);
                requested -= requested % channels;
                if (requested == 0) break;
                var read = _source.Read(buffer, offset + written, requested);
                // A shorter input, or decoder rounding at EOF, contributes silence.
                if (Enabled && read < requested)
                {
                    Array.Clear(buffer, offset + written + read, requested - read);
                    read = requested;
                }

                _frame += read / channels;
                written += read;
                if (read < requested) break;
            }

            return written;
        }
    }

    /// <summary>
    ///     リピート区間を設定します。どちらかが null の場合や区間の長さが 0 の場合はリピートを無効にします。
    /// </summary>
    /// <param name="a">区間の一端 (秒)。</param>
    /// <param name="b">区間のもう一端 (秒)。</param>
    internal void SetRange(double? a, double? b)
    {
        lock (_gate)
        {
            _start = (long)Math.Round(Math.Min(a ?? 0, b ?? 0) * WaveFormat.SampleRate);
            _end = (long)Math.Round(Math.Max(a ?? 0, b ?? 0) * WaveFormat.SampleRate);
            Enabled = a.HasValue && b.HasValue && _end > _start;
        }
    }

    /// <summary>
    ///     再生位置を移動します。リピート区間外の場合は区間の開始位置に移動します。
    /// </summary>
    /// <param name="seconds">移動先の位置 (秒)。</param>
    internal void Seek(double seconds)
    {
        lock (_gate)
        {
            _frame = (long)Math.Round(seconds * WaveFormat.SampleRate);
            if (Enabled && (_frame < _start || _frame >= _end)) _frame = _start;
            _seek((double)_frame / WaveFormat.SampleRate);
        }
    }
}

// Variable-rate stereo playback. Resampling changes pitch together with speed.
internal sealed class AudioSpeedProvider : ISampleProvider
{
    private readonly float[] _frameA, _frameB;
    private readonly float[] _readBuffer;
    private readonly ISampleProvider _source;
    private int _buffered, _offset;
    private double _fraction;
    private bool _initialized, _ended;

    /// <summary>
    ///     再生速度を変更できるサンプル プロバイダーを作成します。
    /// </summary>
    /// <param name="source">読み取り元のサンプル プロバイダー。</param>
    internal AudioSpeedProvider(ISampleProvider source)
    {
        _source = source;
        _frameA = new float[WaveFormat.Channels];
        _frameB = new float[WaveFormat.Channels];
        _readBuffer = new float[4096 * WaveFormat.Channels];
    }

    internal double Speed { get; set; } = 1;
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>
    ///     隣接フレーム間を線形補間しながら、再生速度に応じてサンプルを読み取ります。速度の変更に合わせて音程も変わります。
    /// </summary>
    /// <param name="buffer">書き込み先のバッファー。</param>
    /// <param name="offset">書き込み開始位置。</param>
    /// <param name="count">読み取るサンプル数。</param>
    /// <returns>書き込んだサンプル数。</returns>
    public int Read(float[] buffer, int offset, int count)
    {
        if (_ended) return 0;
        if (!_initialized)
        {
            if (!Next(_frameA))
            {
                _ended = true;
                return 0;
            }

            if (!Next(_frameB)) Array.Copy(_frameA, _frameB, _frameA.Length);
            _initialized = true;
        }

        var channels = WaveFormat.Channels;
        var written = 0;
        while (written + channels <= count && !_ended)
        {
            for (var c = 0; c < channels; c++)
                buffer[offset + written + c] = (float)(_frameA[c] + (_frameB[c] - _frameA[c]) * _fraction);
            written += channels;
            _fraction += Speed;
            while (_fraction >= 1)
            {
                _fraction -= 1;
                Array.Copy(_frameB, _frameA, channels);
                if (!Next(_frameB))
                {
                    _ended = true;
                    break;
                }
            }
        }

        return written;
    }

    /// <summary>
    ///     補間の状態と内部バッファーを初期化します。
    /// </summary>
    internal void Reset()
    {
        _buffered = _offset = 0;
        _fraction = 0;
        _initialized = _ended = false;
    }

    /// <summary>
    ///     読み取り元から次の 1 フレームを取得します。
    /// </summary>
    /// <param name="frame">フレームの書き込み先。</param>
    /// <returns>フレームを取得できた場合は true。</returns>
    private bool Next(float[] frame)
    {
        if (_offset >= _buffered)
        {
            _buffered = _source.Read(_readBuffer, 0, _readBuffer.Length);
            _offset = 0;
            if (_buffered == 0) return false;
        }

        for (var c = 0; c < frame.Length; c++) frame[c] = _readBuffer[_offset++];
        return true;
    }
}

internal sealed class AudioDiffPlayer : IDisposable
{
    private readonly double[] _delays = new double[2];
    private readonly MixingSampleProvider _mixer;
    private readonly WaveOutEvent _output;
    private readonly float[] _peaks = new float[2];
    private readonly Stopwatch _playbackClock = new();
    private readonly AudioFileReader[] _readers = new AudioFileReader[2];
    private readonly AudioRepeatProvider _repeat;
    private readonly AudioSpeedProvider _speedProvider;
    private readonly Lock _syncLock = new();
    private readonly VolumeSampleProvider[] _volumes = new VolumeSampleProvider[2];

    internal Exception PlaybackError;
    private double _playbackSpeed = 1;
    private double _timelineOrigin, _repeatStart, _repeatEnd;

    /// <summary>
    ///     比較元と比較先を 48 kHz ステレオにそろえてミキシングし、リピート・速度変更付きで再生するプレーヤーを作成します。
    /// </summary>
    /// <param name="source">比較元の音声ファイル。</param>
    /// <param name="target">比較先の音声ファイル。</param>
    /// <param name="duration">再生全体の長さ (秒)。</param>
    internal AudioDiffPlayer(string source, string target, double duration)
    {
        Duration = duration;
        // ReadFully を false にすることで、両方の入力が終了した際に 0 バイト（末尾）を返す
        _mixer = new MixingSampleProvider(WaveFormat) { ReadFully = false };

        string[] paths = { source, target };
        for (var i = 0; i < 2; i++)
        {
            if (string.IsNullOrEmpty(paths[i]) || !File.Exists(paths[i])) continue;

            var reader = new AudioFileReader(paths[i]);
            _readers[i] = reader;

            ISampleProvider provider = reader;
            if (reader.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
            if (provider.WaveFormat.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);

            var vol = new VolumeSampleProvider(provider) { Volume = 1.0f };
            _volumes[i] = vol;
            _mixer.AddMixerInput(Meter(i, vol));
        }

        var waveOut = new WaveOutEvent
        {
            DesiredLatency = 150,
            NumberOfBuffers = 2
        };
        waveOut.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) Volatile.Write(ref PlaybackError, e.Exception);
        };
        _repeat = new AudioRepeatProvider(_mixer, ResetInputs);
        _speedProvider = new AudioSpeedProvider(_repeat);
        waveOut.Init(new SampleToWaveProvider(_speedProvider));
        _output = waveOut;

        Seek(0);
    }

    internal double Duration { get; private set; }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    internal bool Playing => _output?.PlaybackState == PlaybackState.Playing;

    internal double Position
    {
        get
        {
            lock (_syncLock)
            {
                // Use elapsed playback time without depending on the driver's byte
                // position or output format to determine the display speed.
                // Stop/Pause/Seek explicitly stop this clock; a running clock with a
                // stopped output therefore means playback drained naturally (or failed).
                if (_playbackClock.IsRunning && _output.PlaybackState == PlaybackState.Stopped)
                {
                    _playbackClock.Stop();
                    if (_repeatEnd <= _repeatStart && Volatile.Read(ref PlaybackError) == null)
                    {
                        _timelineOrigin = Duration;
                        _playbackClock.Reset();
                    }
                }

                return PlaybackPosition(_timelineOrigin, _playbackClock.Elapsed.TotalSeconds * _playbackSpeed,
                    _repeatStart,
                    _repeatEnd, Duration);
            }
        }
    }

    /// <summary>
    ///     再生を停止し、出力デバイスと音声ファイルを解放します。
    /// </summary>
    public void Dispose()
    {
        lock (_syncLock)
        {
            _playbackClock.Stop();
            _output?.Stop();
            _output?.Dispose();

            for (var i = 0; i < _readers.Length; i++)
            {
                _readers[i]?.Dispose();
                _readers[i] = null;
                _volumes[i] = null;
            }
        }
    }

    /// <summary>
    ///     指定側の再生遅延を取得します。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <returns>遅延 (秒)。</returns>
    internal double GetDelay(int side)
    {
        return _delays[side];
    }

    /// <summary>
    ///     指定側の再生遅延を設定し、再生全体の長さとリピート区間を調整して、現在の位置から再生を続けます。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <param name="seconds">遅延 (秒)。負の値は先行を表します。</param>
    internal void SetDelay(int side, double seconds)
    {
        if (side < 0 || side >= 2 || !double.IsFinite(seconds)) throw new ArgumentOutOfRangeException();
        lock (_syncLock)
        {
            if (Math.Abs(_delays[side] - seconds) < 1e-9) return;
            var position = Position;
            var playing = Playing;
            _playbackClock.Stop();
            _output.Stop();
            _delays[side] = seconds;
            Duration = Math.Max(0, Math.Max(_readers[0] == null ? 0 : _readers[0].TotalTime.TotalSeconds + _delays[0],
                _readers[1] == null ? 0 : _readers[1].TotalTime.TotalSeconds + _delays[1]));
            if (_repeatEnd > Duration)
            {
                _repeatEnd = Duration;
                if (_repeatStart >= _repeatEnd) _repeatStart = _repeatEnd = 0;
                _repeat.SetRange(_repeatStart, _repeatEnd);
            }

            _repeat.Seek(Math.Min(position, Duration));
            ResetPositionClock();
            if (playing && position < Duration) StartPlayback();
        }
    }

    /// <summary>
    ///     再生開始位置と経過時間から、リピート区間を考慮した現在の再生位置を求めます。
    /// </summary>
    /// <param name="origin">再生開始時の位置 (秒)。</param>
    /// <param name="elapsed">再生速度を掛けた経過時間 (秒)。</param>
    /// <param name="start">リピート区間の開始 (秒)。</param>
    /// <param name="end">リピート区間の終了 (秒)。</param>
    /// <param name="duration">再生全体の長さ (秒)。</param>
    /// <returns>現在の再生位置 (秒)。</returns>
    internal static double PlaybackPosition(double origin, double elapsed, double start, double end, double duration)
    {
        var position = origin + Math.Max(0, elapsed);
        if (end > start && position >= end)
            position = start + (position - start) % (end - start);
        return Math.Clamp(position, 0, duration);
    }

    /// <summary>
    ///     再生位置の計測用時計をリセットし、現在のリピート プロバイダーの位置を基準にします。
    /// </summary>
    private void ResetPositionClock()
    {
        _playbackClock.Reset();
        _timelineOrigin = _repeat.Position;
        _speedProvider?.Reset();
    }

    /// <summary>
    ///     再生中の指定側の直近のピーク レベルを取得します。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <returns>ピーク レベル。再生していない場合は 0。</returns>
    internal float GetPeak(int side)
    {
        return Playing && _readers[side] != null &&
               _readers[side].CurrentTime < _readers[side].TotalTime
            ? Volatile.Read(ref _peaks[side])
            : 0;
    }

    /// <summary>
    ///     ピーク レベルを記録するメーターをサンプル プロバイダーに追加します。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <param name="provider">計測対象のサンプル プロバイダー。</param>
    /// <returns>メーター付きのサンプル プロバイダー。</returns>
    private ISampleProvider Meter(int side, ISampleProvider provider)
    {
        var meter = new MeteringSampleProvider(provider) { SamplesPerNotification = 1440 };
        meter.StreamVolume += (_, e) =>
        {
            float peak = 0;
            foreach (var sample in e.MaxSampleValues) peak = Math.Max(peak, sample);
            Volatile.Write(ref _peaks[side], peak);
        };
        return meter;
    }

    /// <summary>
    ///     再生速度を変更し、現在の位置から再生を続けます。
    /// </summary>
    /// <param name="value">再生速度の倍率。</param>
    internal void SetSpeed(double value)
    {
        lock (_syncLock)
        {
            if (Math.Abs(_playbackSpeed - value) < 0.00001) return;
            var wasPlaying = Playing;
            var position = Position;
            _playbackClock.Stop();
            _output.Stop();
            _playbackSpeed = value;
            _speedProvider.Speed = value;
            _repeat.Seek(position);
            ResetPositionClock();
            if (wasPlaying) StartPlayback();
        }
    }

    /// <summary>
    ///     指定側の音量を 0 から 1 の範囲で設定します。
    /// </summary>
    /// <param name="side">0 が比較元、1 が比較先。</param>
    /// <param name="value">音量。</param>
    internal void SetVolume(int side, double value)
    {
        if (side >= 0 && side < 2 && _volumes[side] != null) _volumes[side].Volume = (float)Math.Clamp(value, 0, 1);
    }

    /// <summary>
    ///     出力を停止してバッファーを破棄し、指定位置へ移動します。再生中だった場合は再開します。
    /// </summary>
    /// <param name="seconds">移動先の位置 (秒)。</param>
    internal void Seek(double seconds)
    {
        lock (_syncLock)
        {
            var wasPlaying = Playing;
            _playbackClock.Stop();
            _output.Stop(); // Seek時は一度停止してドライバの内部バッファをクリア

            _repeat.Seek(Math.Clamp(seconds, 0, Duration));
            ResetPositionClock();
            if (wasPlaying && Position < Duration) StartPlayback();
        }
    }

    /// <summary>
    ///     リピート区間をサンプル単位にそろえて設定し、現在の位置から再生を続けます。
    /// </summary>
    /// <param name="a">区間の一端 (秒)。null の場合はリピートを解除します。</param>
    /// <param name="b">区間のもう一端 (秒)。null の場合はリピートを解除します。</param>
    internal void SetRepeat(double? a, double? b)
    {
        lock (_syncLock)
        {
            var wasPlaying = Playing;
            var position = Position;
            _playbackClock.Stop();
            _output.Stop();
            _repeat.SetRange(a, b);
            if (_repeat.Enabled && a.HasValue && b.HasValue)
            {
                _repeatStart = Math.Round(Math.Min(a.Value, b.Value) * WaveFormat.SampleRate)
                               / WaveFormat.SampleRate;
                _repeatEnd = Math.Round(Math.Max(a.Value, b.Value) * WaveFormat.SampleRate)
                             / WaveFormat.SampleRate;
            }
            else
            {
                _repeatStart = 0;
                _repeatEnd = 0;
            }

            _repeat.Seek(position);
            ResetPositionClock();
            if (wasPlaying) StartPlayback();
        }
    }

    /// <summary>
    ///     各入力を遅延を考慮した位置にシークし、リサンプラーの履歴を破棄するためにミキサーの入力を作り直します。
    /// </summary>
    /// <param name="target">移動先のタイムライン上の位置 (秒)。</param>
    private void ResetInputs(double target)
    {
        _mixer.RemoveAllMixerInputs();
        for (var i = 0; i < 2; i++)
        {
            Volatile.Write(ref _peaks[i], 0);
            var reader = _readers[i];
            if (reader == null) continue;

            var local = target - _delays[i];
            if (local < reader.TotalTime.TotalSeconds)
                reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, local));
            else
                reader.CurrentTime = reader.TotalTime;
            // Discard resampler history on seek so buffered pre-seek samples cannot leak.
            var gain = _volumes[i]?.Volume ?? 1;
            ISampleProvider provider = reader;
            if (reader.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
            if (provider.WaveFormat.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
            if (local < 0) provider = new OffsetSampleProvider(provider) { DelayBy = TimeSpan.FromSeconds(-local) };
            _volumes[i] = new VolumeSampleProvider(provider) { Volume = gain };
            if (local < reader.TotalTime.TotalSeconds) _mixer.AddMixerInput(Meter(i, _volumes[i]));
        }
    }

    /// <summary>
    ///     再生を開始します。末尾に達している場合は先頭に戻してから再生します。
    /// </summary>
    internal void Play()
    {
        lock (_syncLock)
        {
            // 末尾付近（残り0.05秒以下）または再生完了状態なら先頭に巻き戻す
            if (Position >= Duration) Seek(0);
            StartPlayback();
        }
    }

    /// <summary>
    ///     出力デバイスの再生と再生位置の計測を開始します。
    /// </summary>
    private void StartPlayback()
    {
        _output.Play();
        _playbackClock.Start();
    }

    /// <summary>
    ///     再生を一時停止します。
    /// </summary>
    internal void Pause()
    {
        lock (_syncLock)
        {
            _output?.Pause();
            _playbackClock.Stop();
        }
    }
}