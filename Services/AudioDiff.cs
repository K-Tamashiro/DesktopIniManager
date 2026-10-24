using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopIniManager.Services;

// Compare decoded amplitudes, independent of playback gain and display resolution.
internal sealed class AudioDifferences
{
    internal readonly System.Collections.Generic.List<(double Start, double End)>[] Channels;
    internal System.Collections.Generic.List<(double Start, double End)>[] TargetChannels;
    private AudioDifferences(int channels)
    {
        Channels = new System.Collections.Generic.List<(double, double)>[channels];
        for (int ch = 0; ch < channels; ch++) Channels[ch] = new();
        TargetChannels = Channels;
    }

    internal static AudioDifferences Load(string source, string target, CancellationToken cancellation)
    {
        using var a = !string.IsNullOrEmpty(source) && File.Exists(source) ? new AudioFileReader(source) : null;
        using var b = !string.IsNullOrEmpty(target) && File.Exists(target) ? new AudioFileReader(target) : null;
        int rate = Math.Max(a?.WaveFormat.SampleRate ?? 0, b?.WaveFormat.SampleRate ?? 0);
        int channels = Math.Max(a?.WaveFormat.Channels ?? 0, b?.WaveFormat.Channels ?? 0);
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
        int windowFrames = Math.Max(1, (int)Math.Round(rate * 0.01));
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
            Array.Clear(energyA); Array.Clear(energyB);
            Array.Clear(countA); Array.Clear(countB);
            long first = frame;
            for (int i = 0; i < windowFrames; i++, frame++)
            {
                bool hasA = inputA?.NextFrame() == true;
                bool hasB = inputB?.NextFrame() == true;
                if (!hasA && !hasB) break;
                for (int ch = 0; ch < channels; ch++)
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
            for (int ch = 0; ch < channels; ch++)
            {
                double rmsA = countA[ch] == 0 ? 0 : Math.Sqrt(energyA[ch] / countA[ch]);
                double rmsB = countB[ch] == 0 ? 0 : Math.Sqrt(energyB[ch] / countB[ch]);
                bool different = countA[ch] != countB[ch] ||
                    Math.Abs(rmsA - rmsB) > Math.Max(absoluteTolerance, Math.Max(rmsA, rmsB) * relativeTolerance);
                if (different && starts[ch] < 0) starts[ch] = first;
                if (!different && starts[ch] >= 0)
                {
                    result.Channels[ch].Add(((double)starts[ch] / rate, (double)first / rate));
                    starts[ch] = -1;
                }
            }
        }
        for (int ch = 0; ch < channels; ch++)
            if (starts[ch] >= 0)
                result.Channels[ch].Add(((double)starts[ch] / rate, (double)frame / rate));
        return result;
    }

    // Buffered, sequential sample reader; no per-frame allocation or copying.
    private sealed class FrameStream
    {
        private readonly ISampleProvider source;
        private readonly float[] buffer;
        private int offset, available;
        private bool ended;
        private int frameOffset;
        internal int Channels { get; }

        internal FrameStream(ISampleProvider source)
        {
            this.source = source;
            Channels = source.WaveFormat.Channels;
            buffer = new float[16384 * Channels];
        }

        internal bool NextFrame()
        {
            if (ended) return false;
            if (offset >= available)
            {
                available = source.Read(buffer, 0, buffer.Length);
                offset = 0;
                if (available == 0) { ended = true; return false; }
                if (available % Channels != 0)
                    throw new InvalidDataException("Incomplete audio frame.");
            }
            frameOffset = offset;
            offset += Channels;
            return true;
        }

        internal float Sample(int channel) => buffer[frameOffset + channel];
    }
}

// Bounded peak envelope: decoding is streamed, never retained as a full PCM file.
internal sealed class AudioEnvelope
{
    internal float[][] Min, Max;
    internal DesktopIniManager.Controls.WaveformData Data;
    internal double Duration;
    internal int Channels;
    internal int SampleRate;
    internal int? BitDepth;
    internal long? BitRate;
    internal bool FloatingPoint;

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
                double length = (double)frame.SampleCount / frame.SampleRate;
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
            format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (format.Encoding == WaveFormatEncoding.Pcm || FloatingPoint ||
            format is WaveFormatExtensible pcm && pcm.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"))
            BitDepth = format.BitsPerSample;
        BitRate = (long)format.AverageBytesPerSecond * 8;
    }

    internal static AudioEnvelope Load(string path, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var data = DesktopIniManager.Controls.WaveformData.Load(path, cancellation);
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
    private readonly ISampleProvider source;
    private readonly Action<double> seek;
    private readonly object gate = new();
    private long frame, start, end;
    internal bool Enabled { get; private set; }
    public WaveFormat WaveFormat => source.WaveFormat;
    internal double Position { get { lock (gate) return (double)frame / WaveFormat.SampleRate; } }
    internal AudioRepeatProvider(ISampleProvider source, Action<double> seek)
    {
        this.source = source;
        this.seek = seek;
    }
    internal void SetRange(double? a, double? b)
    {
        lock (gate)
        {
            start = (long)Math.Round(Math.Min(a ?? 0, b ?? 0) * WaveFormat.SampleRate);
            end = (long)Math.Round(Math.Max(a ?? 0, b ?? 0) * WaveFormat.SampleRate);
            Enabled = a.HasValue && b.HasValue && end > start;
        }
    }
    internal void Seek(double seconds)
    {
        lock (gate)
        {
            frame = (long)Math.Round(seconds * WaveFormat.SampleRate);
            if (Enabled && (frame < start || frame >= end)) frame = start;
            seek((double)frame / WaveFormat.SampleRate);
        }
    }
    public int Read(float[] buffer, int offset, int count)
    {
        lock (gate)
        {
            int written = 0, channels = WaveFormat.Channels;
            while (written < count)
            {
                if (Enabled && (frame < start || frame >= end))
                {
                    frame = start;
                    seek((double)frame / WaveFormat.SampleRate);
                }
                int requested = count - written;
                if (Enabled) requested = (int)Math.Min(requested, (end - frame) * channels);
                requested -= requested % channels;
                if (requested == 0) break;
                int read = source.Read(buffer, offset + written, requested);
                // A shorter input, or decoder rounding at EOF, contributes silence.
                if (Enabled && read < requested)
                {
                    Array.Clear(buffer, offset + written + read, requested - read);
                    read = requested;
                }
                frame += read / channels;
                written += read;
                if (read < requested) break;
            }
            return written;
        }
    }
}

// Variable-rate stereo playback. Resampling changes pitch together with speed.
internal sealed class AudioSpeedProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly float[] frameA, frameB;
    private readonly float[] readBuffer;
    private int buffered, offset;
    private double fraction;
    private bool initialized, ended;
    internal double Speed { get; set; } = 1;
    public WaveFormat WaveFormat => source.WaveFormat;

    internal AudioSpeedProvider(ISampleProvider source)
    {
        this.source = source;
        frameA = new float[WaveFormat.Channels];
        frameB = new float[WaveFormat.Channels];
        readBuffer = new float[4096 * WaveFormat.Channels];
    }

    internal void Reset()
    {
        buffered = offset = 0;
        fraction = 0;
        initialized = ended = false;
    }

    private bool Next(float[] frame)
    {
        if (offset >= buffered)
        {
            buffered = source.Read(readBuffer, 0, readBuffer.Length);
            offset = 0;
            if (buffered == 0) return false;
        }
        for (int c = 0; c < frame.Length; c++) frame[c] = readBuffer[offset++];
        return true;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (ended) return 0;
        if (!initialized)
        {
            if (!Next(frameA)) { ended = true; return 0; }
            if (!Next(frameB)) Array.Copy(frameA, frameB, frameA.Length);
            initialized = true;
        }
        int channels = WaveFormat.Channels;
        int written = 0;
        while (written + channels <= count && !ended)
        {
            for (int c = 0; c < channels; c++)
                buffer[offset + written + c] = (float)(frameA[c] + (frameB[c] - frameA[c]) * fraction);
            written += channels;
            fraction += Speed;
            while (fraction >= 1)
            {
                fraction -= 1;
                Array.Copy(frameB, frameA, channels);
                if (!Next(frameB)) { ended = true; break; }
            }
        }
        return written;
    }
}

internal sealed class AudioDiffPlayer : IDisposable
{
    private readonly object syncLock = new();
    private readonly AudioFileReader[] readers = new AudioFileReader[2];
    private readonly VolumeSampleProvider[] volumes = new VolumeSampleProvider[2];
    private readonly MixingSampleProvider mixer;
    private readonly WaveOutEvent output;
    private readonly AudioRepeatProvider repeat;
    private readonly AudioSpeedProvider speedProvider;
    private double playbackSpeed = 1;
    private readonly double[] delays = new double[2];
    internal double GetDelay(int side) => delays[side];
    internal void SetDelay(int side, double seconds)
    {
        if (side < 0 || side >= 2 || !double.IsFinite(seconds)) throw new ArgumentOutOfRangeException();
        lock (syncLock)
        {
            if (delays[side] == seconds) return;
            double position = Position;
            bool playing = Playing;
            playbackClock.Stop();
            output.Stop();
            delays[side] = seconds;
            Duration = Math.Max(0, Math.Max(readers[0] == null ? 0 : readers[0].TotalTime.TotalSeconds + delays[0],
                readers[1] == null ? 0 : readers[1].TotalTime.TotalSeconds + delays[1]));
            if (repeatEnd > Duration)
            {
                repeatEnd = Duration;
                if (repeatStart >= repeatEnd) repeatStart = repeatEnd = 0;
                repeat.SetRange(repeatStart, repeatEnd);
            }
            repeat.Seek(Math.Min(position, Duration));
            ResetPositionClock();
            if (playing && position < Duration) StartPlayback();
        }
    }
    private readonly System.Diagnostics.Stopwatch playbackClock = new();
    private double timelineOrigin, repeatStart, repeatEnd;

    internal static double PlaybackPosition(double origin, double elapsed, double start, double end, double duration)
    {
        double position = origin + Math.Max(0, elapsed);
        if (end > start && position >= end)
            position = start + (position - start) % (end - start);
        return Math.Clamp(position, 0, duration);
    }

    private void ResetPositionClock()
    {
        playbackClock.Reset();
        timelineOrigin = repeat.Position;
        speedProvider?.Reset();
    }
    private readonly float[] peaks = new float[2];

    internal float GetPeak(int side) => Playing && readers[side] != null &&
        readers[side].CurrentTime < readers[side].TotalTime ? Volatile.Read(ref peaks[side]) : 0;

    private ISampleProvider Meter(int side, ISampleProvider provider)
    {
        var meter = new MeteringSampleProvider(provider) { SamplesPerNotification = 1440 };
        meter.StreamVolume += (_, e) =>
        {
            float peak = 0;
            foreach (float sample in e.MaxSampleValues) peak = Math.Max(peak, sample);
            Volatile.Write(ref peaks[side], peak);
        };
        return meter;
    }

    internal Exception PlaybackError;
    internal double Duration { get; private set; }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    internal bool Playing => output?.PlaybackState == PlaybackState.Playing;

    internal double Position
    {
        get
        {
            lock (syncLock)
            {
                // Use elapsed playback time without depending on the driver's byte
                // position or output format to determine the display speed.
                // Stop/Pause/Seek explicitly stop this clock; a running clock with a
                // stopped output therefore means playback drained naturally (or failed).
                if (playbackClock.IsRunning && output.PlaybackState == PlaybackState.Stopped)
                {
                    playbackClock.Stop();
                    if (repeatEnd <= repeatStart && Volatile.Read(ref PlaybackError) == null)
                    {
                        timelineOrigin = Duration;
                        playbackClock.Reset();
                    }
                }
                return PlaybackPosition(timelineOrigin, playbackClock.Elapsed.TotalSeconds * playbackSpeed, repeatStart, repeatEnd, Duration);
            }
        }
    }

    internal AudioDiffPlayer(string source, string target, double duration)
    {
        Duration = duration;
        // ReadFully を false にすることで、両方の入力が終了した際に 0 バイト（末尾）を返す
        mixer = new MixingSampleProvider(WaveFormat) { ReadFully = false };

        string[] paths = { source, target };
        for (int i = 0; i < 2; i++)
        {
            if (string.IsNullOrEmpty(paths[i]) || !File.Exists(paths[i])) continue;

            var reader = new AudioFileReader(paths[i]);
            readers[i] = reader;

            ISampleProvider provider = reader;
            if (reader.WaveFormat.Channels == 1)
            {
                provider = new MonoToStereoSampleProvider(provider);
            }
            if (provider.WaveFormat.SampleRate != 48000)
            {
                provider = new WdlResamplingSampleProvider(provider, 48000);
            }

            var vol = new VolumeSampleProvider(provider) { Volume = 1.0f };
            volumes[i] = vol;
            mixer.AddMixerInput(Meter(i, vol));
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
        repeat = new AudioRepeatProvider(mixer, ResetInputs);
        speedProvider = new AudioSpeedProvider(repeat);
        waveOut.Init(new SampleToWaveProvider(speedProvider));
        output = waveOut;

        Seek(0);
    }

    internal void SetSpeed(double value)
    {
        lock (syncLock)
        {
            if (Math.Abs(playbackSpeed - value) < 0.00001) return;
            bool wasPlaying = Playing;
            double position = Position;
            playbackClock.Stop();
            output.Stop();
            playbackSpeed = value;
            speedProvider.Speed = value;
            repeat.Seek(position);
            ResetPositionClock();
            if (wasPlaying) StartPlayback();
        }
    }

    internal void SetVolume(int side, double value)
    {
        if (side >= 0 && side < 2 && volumes[side] != null)
        {
            volumes[side].Volume = (float)Math.Clamp(value, 0, 1);
        }
    }

    internal void Seek(double seconds)
    {
        lock (syncLock)
        {
            bool wasPlaying = Playing;
            playbackClock.Stop();
            output.Stop(); // Seek時は一度停止してドライバの内部バッファをクリア

            repeat.Seek(Math.Clamp(seconds, 0, Duration));
            ResetPositionClock();
            if (wasPlaying && Position < Duration) StartPlayback();
        }
    }

    internal void SetRepeat(double? a, double? b)
    {
        lock (syncLock)
        {
            bool wasPlaying = Playing;
            double position = Position;
            playbackClock.Stop();
            output.Stop();
            repeat.SetRange(a, b);
            repeatStart = repeat.Enabled ? Math.Round(Math.Min(a.Value, b.Value) * WaveFormat.SampleRate) / WaveFormat.SampleRate : 0;
            repeatEnd = repeat.Enabled ? Math.Round(Math.Max(a.Value, b.Value) * WaveFormat.SampleRate) / WaveFormat.SampleRate : 0;
            repeat.Seek(position);
            ResetPositionClock();
            if (wasPlaying) StartPlayback();
        }
    }

    private void ResetInputs(double target)
    {
        mixer.RemoveAllMixerInputs();
        for (int i = 0; i < 2; i++)
        {
            Volatile.Write(ref peaks[i], 0);
            var reader = readers[i];
            if (reader == null) continue;

            double local = target - delays[i];
            if (local < reader.TotalTime.TotalSeconds)
            {
                reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, local));
            }
            else
            {
                reader.CurrentTime = reader.TotalTime;
            }
            // Discard resampler history on seek so buffered pre-seek samples cannot leak.
            float gain = volumes[i]?.Volume ?? 1;
            ISampleProvider provider = reader;
            if (reader.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
            if (provider.WaveFormat.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
            if (local < 0) provider = new OffsetSampleProvider(provider) { DelayBy = TimeSpan.FromSeconds(-local) };
            volumes[i] = new VolumeSampleProvider(provider) { Volume = gain };
            if (local < reader.TotalTime.TotalSeconds) mixer.AddMixerInput(Meter(i, volumes[i]));
        }

    }

    internal void Play()
    {
        lock (syncLock)
        {
            // 末尾付近（残り0.05秒以下）または再生完了状態なら先頭に巻き戻す
            if (Position >= Duration)
            {
                Seek(0);
            }
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        output.Play();
        playbackClock.Start();
    }

    internal void Pause()
    {
        lock (syncLock)
        {
            output?.Pause();
            playbackClock.Stop();
        }
    }

    public void Dispose()
    {
        lock (syncLock)
        {
            playbackClock.Stop();
            output?.Stop();
            output?.Dispose();

            for (int i = 0; i < readers.Length; i++)
            {
                readers[i]?.Dispose();
                readers[i] = null;
                volumes[i] = null;
            }
        }
    }
}
