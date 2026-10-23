using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace DesktopIniManager.Controls;

/// <summary>Immutable, fixed-time min/max bins. No PCM is retained.</summary>
public sealed class WaveformData
{
    public const int DefaultBinsPerSecond = 2048;
    private const int BlockSize = 64;
    private readonly float[][] minimum, maximum;
    private readonly float[][] blockMinimum, blockMaximum;
    private readonly int leafCount;
    public int Channels => minimum.Length;
    public int SampleRate { get; }
    public int BinsPerSecond { get; }
    public int BinCount => minimum[0].Length;
    public double Duration { get; }
    public float Minimum(int channel, int bin) => minimum[channel][bin];
    public float Maximum(int channel, int bin) => maximum[channel][bin];

    private WaveformData(float[][] min, float[][] max, int rate, int density, long frames, CancellationToken cancellation)
    {
        minimum = min; maximum = max; SampleRate = rate; BinsPerSecond = density;
        Duration = (double)frames / rate;
        int blocks = (BinCount + BlockSize - 1) / BlockSize;
        leafCount = 1;
        while (leafCount < blocks) leafCount *= 2;
        blockMinimum = new float[Channels][]; blockMaximum = new float[Channels][];
        for (int ch = 0; ch < Channels; ch++)
        {
            var lo = blockMinimum[ch] = new float[leafCount * 2];
            var hi = blockMaximum[ch] = new float[leafCount * 2];
            Array.Fill(lo, float.PositiveInfinity); Array.Fill(hi, float.NegativeInfinity);
            for (int block = 0; block < blocks; block++)
            {
                cancellation.ThrowIfCancellationRequested();
                int end = Math.Min(BinCount, (block + 1) * BlockSize), node = leafCount + block;
                for (int bin = block * BlockSize; bin < end; bin++)
                {
                    lo[node] = Math.Min(lo[node], min[ch][bin]);
                    hi[node] = Math.Max(hi[node], max[ch][bin]);
                }
            }
            for (int node = leafCount - 1; node > 0; node--)
            {
                if ((node & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                lo[node] = Math.Min(lo[node * 2], lo[node * 2 + 1]);
                hi[node] = Math.Max(hi[node * 2], hi[node * 2 + 1]);
            }
        }
    }

    // Exact extrema over [first, end). The original bins are never changed or discarded.
    internal void GetExtrema(int channel, int first, int end, out float min, out float max)
    {
        if ((uint)channel >= Channels || first < 0 || end > BinCount || first >= end)
            throw new ArgumentOutOfRangeException();
        min = float.PositiveInfinity; max = float.NegativeInfinity;
        while (first < end && first % BlockSize != 0)
        {
            min = Math.Min(min, minimum[channel][first]); max = Math.Max(max, maximum[channel][first++]);
        }
        while (first < end && end % BlockSize != 0)
        {
            --end; min = Math.Min(min, minimum[channel][end]); max = Math.Max(max, maximum[channel][end]);
        }
        int left = leafCount + first / BlockSize, right = leafCount + end / BlockSize;
        var lo = blockMinimum[channel]; var hi = blockMaximum[channel];
        while (left < right)
        {
            if ((left & 1) != 0) { min = Math.Min(min, lo[left]); max = Math.Max(max, hi[left++]); }
            if ((right & 1) != 0) { --right; min = Math.Min(min, lo[right]); max = Math.Max(max, hi[right]); }
            left /= 2; right /= 2;
        }
    }

    public static WaveformData Load(string path, CancellationToken cancellation = default, int binsPerSecond = DefaultBinsPerSecond)
    {
        using var reader = new AudioFileReader(path);
        // Limit retained waveform bins, not the duration of the audio.
        const int maximumBins = 2_000_000;
        if (reader.TotalTime.TotalSeconds > 0)
            binsPerSecond = Math.Min(binsPerSecond,
                Math.Max(1, (int)Math.Floor(maximumBins / reader.TotalTime.TotalSeconds)));
        return Read(reader, cancellation, binsPerSecond);
    }

    public static WaveformData Read(ISampleProvider source, CancellationToken cancellation = default, int binsPerSecond = DefaultBinsPerSecond)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        int rate = source.WaveFormat.SampleRate, channels = source.WaveFormat.Channels;
        if (binsPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(binsPerSecond));
        if (rate < 1 || channels < 1) throw new ArgumentException("Invalid audio format.", nameof(source));
        // Preserve every sample when the source rate is lower than the requested density.
        binsPerSecond = Math.Min(binsPerSecond, rate);
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
        return new WaveformData(minima, maxima, rate, binsPerSecond, samples / channels, cancellation);
    }
}
