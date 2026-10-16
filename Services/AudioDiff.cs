using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopIniManager.Services;

// Bounded peak envelope: decoding is streamed, never retained as a full PCM file.
internal sealed class AudioEnvelope
{
    internal float[][] Min, Max;
    internal double Duration;
    internal int Channels;

    internal static AudioEnvelope Load(string path, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var reader = new AudioFileReader(path);
        int channels = reader.WaveFormat.Channels;
        if (channels < 1 || channels > 2) throw new InvalidDataException("Audio Diff supports mono and stereo audio.");
        const int bins = 8192;
        var result = new AudioEnvelope
        {
            Channels = channels,
            Duration = reader.TotalTime.TotalSeconds,
            Min = new float[channels][],
            Max = new float[channels][]
        };
        for (int c = 0; c < channels; c++)
        {
            result.Min[c] = new float[bins];
            result.Max[c] = new float[bins];
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
                    result.Min[c][bin] = Math.Min(result.Min[c][bin], sample);
                    result.Max[c][bin] = Math.Max(result.Max[c][bin], sample);
                }
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
            mixer.AddMixerInput(vol);
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
                if (target < reader.TotalTime.TotalSeconds) mixer.AddMixerInput(volumes[i]);
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
