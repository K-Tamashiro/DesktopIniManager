using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace DesktopIniManager.Controls;

/// <summary>Immutable, fixed-time min/max bins. No PCM is retained.</summary>
public sealed class WaveformData
{
    private readonly float[][] minimum, maximum;
    public int Channels => minimum.Length;
    public int SampleRate { get; }
    public int BinsPerSecond { get; }
    public int BinCount => minimum[0].Length;
    public double Duration { get; }
    public float Minimum(int channel, int bin) => minimum[channel][bin];
    public float Maximum(int channel, int bin) => maximum[channel][bin];

    private WaveformData(float[][] min, float[][] max, int rate, int density, long frames)
    {
        minimum = min; maximum = max; SampleRate = rate; BinsPerSecond = density;
        Duration = (double)frames / rate;
    }

    public static WaveformData Load(string path, CancellationToken cancellation = default, int binsPerSecond = 2048)
    {
        using var reader = new AudioFileReader(path);
        if (reader.TotalTime > TimeSpan.FromHours(1))
            throw new System.IO.InvalidDataException($"1時間を超える音声ファイルは読み込めません: {System.IO.Path.GetFileName(path)}");
        return Read(reader, cancellation, binsPerSecond);
    }

    public static WaveformData Read(ISampleProvider source, CancellationToken cancellation = default, int binsPerSecond = 2048)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        int rate = source.WaveFormat.SampleRate, channels = source.WaveFormat.Channels;
        if (binsPerSecond < 1 || binsPerSecond > rate) throw new ArgumentOutOfRangeException(nameof(binsPerSecond));
        var min = new List<float>[channels]; var max = new List<float>[channels];
        for (int ch = 0; ch < channels; ch++) { min[ch] = new(); max[ch] = new(); }
        var buffer = new float[4096 * channels];
        long samples = 0;
        int count;
        cancellation.ThrowIfCancellationRequested();
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int i = 0; i < count; i++, samples++)
            {
                long frame = samples / channels;
                if (frame >= (long)rate * 3600) throw new ArgumentException("Waveforms support audio up to one hour.");
                int ch = (int)(samples % channels), bin = (int)(frame * binsPerSecond / rate);
                while (min[ch].Count <= bin) { min[ch].Add(float.PositiveInfinity); max[ch].Add(float.NegativeInfinity); }
                float value = buffer[i];
                if (!float.IsFinite(value)) continue;
                min[ch][bin] = Math.Min(min[ch][bin], value);
                max[ch][bin] = Math.Max(max[ch][bin], value);
            }
        }
        if (samples % channels != 0) throw new System.IO.InvalidDataException("Incomplete audio frame.");
        var minima = new float[channels][]; var maxima = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            minima[ch] = min[ch].ToArray(); maxima[ch] = max[ch].ToArray();
            for (int i = 0; i < minima[ch].Length; i++)
            {
                if (!float.IsFinite(minima[ch][i])) minima[ch][i] = 0;
                if (!float.IsFinite(maxima[ch][i])) maxima[ch][i] = 0;
            }
        }
        return new WaveformData(minima, maxima, rate, binsPerSecond, samples / channels);
    }
}
