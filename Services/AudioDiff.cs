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
    internal double? SourceJoin, TargetJoin;
    private const float CutSampleTolerance = 1e-5f;

    private static bool NearlyEqual(float a, float b) =>
        Math.Abs(a - b) <= CutSampleTolerance;
    private AudioDifferences(int channels)
    {
        Channels = new System.Collections.Generic.List<(double, double)>[channels];
        for (int ch = 0; ch < channels; ch++) Channels[ch] = new();
        TargetChannels = Channels;
    }

    internal static AudioDifferences Load(string source, string target, CancellationToken cancellation)
    {
        var cut = FindSingleCut(source, target, cancellation);
        if (cut != null) return cut;
        using var a = new Samples(source);
        using var b = new Samples(target);
        int rate = Math.Max(a.Rate, b.Rate);
        int channels = Math.Max(a.Channels, b.Channels);
        var result = new AudioDifferences(channels);
        if (rate == 0) return result;
        double duration = Math.Max(a.Duration, b.Duration);
        // Compare short-time energy instead of individual decoded float samples.
        // Lossy codecs change sample values even when the audible waveform is unchanged.
        const double windowSeconds = 0.01;
        const double absoluteTolerance = 0.012;
        const double relativeTolerance = 0.15;
        int windowFrames = Math.Max(1, (int)Math.Round(rate * windowSeconds));
        long count = (long)Math.Ceiling(duration * rate);
        var starts = new long[channels];
        Array.Fill(starts, -1L);
        var energyA = new double[channels];
        var energyB = new double[channels];
        var samplesA = new int[channels];
        var samplesB = new int[channels];
        for (long first = 0; first < count; first += windowFrames)
        {
            cancellation.ThrowIfCancellationRequested();
            Array.Clear(energyA); Array.Clear(energyB);
            Array.Clear(samplesA); Array.Clear(samplesB);
            long end = Math.Min(count, first + windowFrames);
            for (long frame = first; frame < end; frame++)
            {
                double time = (double)frame / rate;
                a.Move(time); b.Move(time);
                for (int ch = 0; ch < channels; ch++)
                {
                    if (a.Has(ch, time))
                    {
                        double value = a.Value(ch, time);
                        energyA[ch] += value * value;
                        samplesA[ch]++;
                    }
                    if (b.Has(ch, time))
                    {
                        double value = b.Value(ch, time);
                        energyB[ch] += value * value;
                        samplesB[ch]++;
                    }
                }
            }
            for (int ch = 0; ch < channels; ch++)
            {
                double rmsA = samplesA[ch] == 0 ? 0 : Math.Sqrt(energyA[ch] / samplesA[ch]);
                double rmsB = samplesB[ch] == 0 ? 0 : Math.Sqrt(energyB[ch] / samplesB[ch]);
                bool different = Math.Abs(rmsA - rmsB) >
                    Math.Max(absoluteTolerance, Math.Max(rmsA, rmsB) * relativeTolerance);
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
                result.Channels[ch].Add(((double)starts[ch] / rate, duration));
        return result;
    }

    // A contiguous insertion/deletion shifts every later sample. Verify the entire
    // remaining suffix before classifying it as unchanged, across all channels.
    private static AudioDifferences FindSingleCut(string source, string target, CancellationToken cancellation)
    {
        using var a = new Samples(source);
        using var b = new Samples(target);
        if (a.Rate == 0 || a.Rate != b.Rate || a.Channels != b.Channels || a.Frames == b.Frames) return null;
        bool sourceLonger = a.Frames > b.Frames;
        var longer = sourceLonger ? a : b;
        var shorter = sourceLonger ? b : a;
        long removed = longer.Frames - shorter.Frames;
        long first = -1;
        for (long frame = 0; frame < shorter.Frames; frame++)
        {
            if ((frame & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            double shortTime = (double)frame / a.Rate;
            double longTime = (double)(frame + (first >= 0 ? removed : 0)) / a.Rate;
            shorter.Move(shortTime); longer.Move(longTime);
            bool equal = true;
            for (int ch = 0; ch < a.Channels; ch++)
                equal &= NearlyEqual(shorter.Value(ch, shortTime), longer.Value(ch, longTime));
            if (equal) continue;
            if (first >= 0) return null;
            first = frame;
            longTime = (double)(frame + removed) / a.Rate;
            longer.Move(longTime);
            for (int ch = 0; ch < a.Channels; ch++)
                if (!NearlyEqual(shorter.Value(ch, shortTime), longer.Value(ch, longTime))) return null;
        }
        cancellation.ThrowIfCancellationRequested();
        if (first < 0) first = shorter.Frames;
        var result = new AudioDifferences(a.Channels);
        result.TargetChannels = new AudioDifferences(a.Channels).Channels;
        var ranges = sourceLonger ? result.Channels : result.TargetChannels;
        for (int ch = 0; ch < ranges.Length; ch++)
            ranges[ch].Add(((double)first / a.Rate, (double)(first + removed) / a.Rate));
        if (sourceLonger) result.TargetJoin = (double)first / a.Rate;
        else result.SourceJoin = (double)first / a.Rate;
        return result;
    }

    // A bounded streaming window. Interpolate only when the sample rates differ.
    private sealed class Samples : IDisposable
    {
        private readonly AudioFileReader reader;
        private readonly float[] buffer, current, next;
        private int offset, available;
        private long index;
        internal int Rate => reader?.WaveFormat.SampleRate ?? 0;
        internal int Channels => reader?.WaveFormat.Channels ?? 0;
        internal double Duration { get; }
        internal long Frames { get; }
        internal Samples(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            reader = new AudioFileReader(path);
            Frames = reader.Length / reader.WaveFormat.BlockAlign;
            Duration = (double)Frames / Rate;
            buffer = new float[4096 * Channels];
            current = new float[Channels]; next = new float[Channels];
            Read(current); Read(next);
        }
        private void Read(float[] frame)
        {
            if (offset >= available) { available = reader.Read(buffer, 0, buffer.Length); offset = 0; }
            for (int ch = 0; ch < Channels; ch++) frame[ch] = offset < available ? buffer[offset++] : 0;
        }
        internal bool Has(int ch, double time) => ch < Channels && time < Duration;
        internal void Move(double time)
        {
            if (reader == null) return;
            long desired = Math.Min((long)Math.Floor(time * Rate + 1e-7), (long)Math.Ceiling(Duration * Rate));
            while (index < desired) { Array.Copy(next, current, Channels); Read(next); index++; }
        }
        internal float Value(int ch, double time)
        {
            double fraction = Math.Clamp(time * Rate - index, 0, 1);
            if (fraction < 1e-7) return current[ch];
            return (float)(current[ch] + (next[ch] - current[ch]) * fraction);
        }
        public void Dispose() => reader?.Dispose();
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
            Data = data, Channels = data.Channels, SampleRate = data.SampleRate, Duration = data.Duration
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
    internal readonly double Duration;

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

                if (target < reader.TotalTime.TotalSeconds)
                {
                    reader.CurrentTime = TimeSpan.FromSeconds(target);
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
                volumes[i] = new VolumeSampleProvider(provider) { Volume = gain };
                if (target < reader.TotalTime.TotalSeconds) mixer.AddMixerInput(Meter(i, volumes[i]));
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
