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
    internal bool[][] MinimumFirst;
    internal double Duration;
    internal int Channels;
    internal int SampleRate;

    internal static AudioEnvelope Load(string path, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var reader = new AudioFileReader(path);
        int channels = reader.WaveFormat.Channels;
        if (channels < 1 || channels > 2) throw new InvalidDataException("Audio Diff supports mono and stereo audio.");
        // Retain fine time slices for horizontal zoom without retaining the full PCM file.
        const int bins = 131072;
        var result = new AudioEnvelope
        {
            Channels = channels,
            SampleRate = reader.WaveFormat.SampleRate,
            Duration = reader.TotalTime.TotalSeconds,
            Min = new float[channels][],
            Max = new float[channels][],
            MinimumFirst = new bool[channels][]
        };
        for (int c = 0; c < channels; c++)
        {
            result.Min[c] = new float[bins];
            result.Max[c] = new float[bins];
            result.MinimumFirst[c] = new bool[bins];
            Array.Fill(result.Min[c], float.PositiveInfinity);
            Array.Fill(result.Max[c], float.NegativeInfinity);
        }

        long totalFrames = Math.Max(1, reader.Length / (sizeof(float) * channels));
        var buffer = new float[4096 * channels];
        long frame = 0;
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int i = 0; i < count; i += channels, frame++)
            {
                int bin = (int)Math.Min(bins - 1, frame * bins / totalFrames);
                for (int c = 0; c < channels && i + c < count; c++)
                {
                    float sample = buffer[i + c];
                    if (!float.IsFinite(sample)) continue;
                    if (sample < result.Min[c][bin])
                    {
                        result.Min[c][bin] = sample;
                        result.MinimumFirst[c][bin] = false;
                    }
                    if (sample > result.Max[c][bin])
                    {
                        result.Max[c][bin] = sample;
                        result.MinimumFirst[c][bin] = true;
                    }
                }
            }
        }
        // Unpopulated bins represent silence, not infinite extrema.
        for (int c = 0; c < channels; c++)
        {
            for (int bin = 0; bin < bins; bin++)
            {
                if (float.IsPositiveInfinity(result.Min[c][bin])) result.Min[c][bin] = 0;
                if (float.IsNegativeInfinity(result.Max[c][bin])) result.Max[c][bin] = 0;
            }
        }
        result.Duration = (double)frame / reader.WaveFormat.SampleRate;
        return result;
    }
}

// Both inputs feed one output clock; shorter inputs contribute silence.
internal sealed class AudioDiffPlayer : IDisposable
{
    private readonly object syncLock = new();
    private readonly AudioFileReader[] readers = new AudioFileReader[2];
    private readonly VolumeSampleProvider[] volumes = new VolumeSampleProvider[2];
    private readonly MixingSampleProvider mixer;
    private readonly IWavePlayer output;
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
                double pos = 0;
                for (int i = 0; i < 2; i++)
                {
                    if (readers[i] != null)
                    {
                        pos = Math.Max(pos, readers[i].CurrentTime.TotalSeconds);
                    }
                }
                return Math.Clamp(pos, 0, Duration);
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
        waveOut.Init(new SampleToWaveProvider(mixer));
        output = waveOut;

        Seek(0);
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
            output.Stop(); // Seek時は一度停止してドライバの内部バッファをクリア

            double target = Math.Clamp(seconds, 0, Duration);
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

            if (wasPlaying && target < Duration)
            {
                output.Play();
            }
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
            output.Play();
        }
    }

    internal void Pause()
    {
        lock (syncLock)
        {
            output?.Pause();
        }
    }

    public void Dispose()
    {
        lock (syncLock)
        {
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
