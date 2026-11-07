using DesktopIniManager.Properties;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DesktopIniManager.Controls;

/// <summary>Immutable, fixed-time min/max bins. No PCM is retained.</summary>
public sealed class WaveformData
{
    public const int DefaultBinsPerSecond = 2048;
    private const int BlockSize = 64;
    private readonly float[][] _blockMinimum, _blockMaximum;
    private readonly int _leafCount;
    private readonly float[][] _minimum, _maximum;

    /// <summary>
    ///     チャンネルごとのビン最小値・最大値を受け取り、区間極値を高速に求めるためのブロック単位のセグメント木を構築します。
    /// </summary>
    /// <param name="min">チャンネルごとのビン最小値。</param>
    /// <param name="max">チャンネルごとのビン最大値。</param>
    /// <param name="rate">元音声のサンプリングレート。</param>
    /// <param name="density">1 秒あたりのビン数。</param>
    /// <param name="frames">元音声の総フレーム数。</param>
    /// <param name="cancellation">構築処理のキャンセル トークン。</param>
    private WaveformData(float[][] min, float[][] max, int rate, int density, long frames,
        CancellationToken cancellation)
    {
        _minimum = min;
        _maximum = max;
        SampleRate = rate;
        BinsPerSecond = density;
        Duration = (double)frames / rate;
        var blocks = (BinCount + BlockSize - 1) / BlockSize;
        _leafCount = 1;
        while (_leafCount < blocks) _leafCount *= 2;
        _blockMinimum = new float[Channels][];
        _blockMaximum = new float[Channels][];
        for (var ch = 0; ch < Channels; ch++)
        {
            var lo = _blockMinimum[ch] = new float[_leafCount * 2];
            var hi = _blockMaximum[ch] = new float[_leafCount * 2];
            Array.Fill(lo, float.PositiveInfinity);
            Array.Fill(hi, float.NegativeInfinity);
            for (var block = 0; block < blocks; block++)
            {
                cancellation.ThrowIfCancellationRequested();
                int end = Math.Min(BinCount, (block + 1) * BlockSize), node = _leafCount + block;
                for (var bin = block * BlockSize; bin < end; bin++)
                {
                    lo[node] = Math.Min(lo[node], min[ch][bin]);
                    hi[node] = Math.Max(hi[node], max[ch][bin]);
                }
            }

            for (var node = _leafCount - 1; node > 0; node--)
            {
                if ((node & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                lo[node] = Math.Min(lo[node * 2], lo[node * 2 + 1]);
                hi[node] = Math.Max(hi[node * 2], hi[node * 2 + 1]);
            }
        }
    }

    public int Channels => _minimum.Length;
    public int SampleRate { get; }
    public int BinsPerSecond { get; }
    public int BinCount => _minimum[0].Length;
    public double Duration { get; }

    /// <summary>
    ///     指定チャンネル・ビンの最小サンプル値を取得します。
    /// </summary>
    /// <param name="channel">チャンネル番号。</param>
    /// <param name="bin">ビン番号。</param>
    /// <returns>ビン内の最小サンプル値。</returns>
    public float Minimum(int channel, int bin)
    {
        return _minimum[channel][bin];
    }

    /// <summary>
    ///     指定チャンネル・ビンの最大サンプル値を取得します。
    /// </summary>
    /// <param name="channel">チャンネル番号。</param>
    /// <param name="bin">ビン番号。</param>
    /// <returns>ビン内の最大サンプル値。</returns>
    public float Maximum(int channel, int bin)
    {
        return _maximum[channel][bin];
    }

    // Exact extrema over [first, end). The original bins are never changed or discarded.
    /// <summary>
    ///     指定チャンネルのビン範囲 [first, end) における正確な最小値と最大値を求めます。
    /// </summary>
    /// <param name="channel">チャンネル番号。</param>
    /// <param name="first">範囲の先頭ビン (含む)。</param>
    /// <param name="end">範囲の終端ビン (含まない)。</param>
    /// <param name="min">範囲内の最小値。</param>
    /// <param name="max">範囲内の最大値。</param>
    internal void GetExtrema(int channel, int first, int end, out float min, out float max)
    {
        if ((uint)channel >= Channels || first < 0 || end > BinCount || first >= end)
            throw new ArgumentOutOfRangeException();
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        while (first < end && first % BlockSize != 0)
        {
            min = Math.Min(min, _minimum[channel][first]);
            max = Math.Max(max, _maximum[channel][first++]);
        }

        while (first < end && end % BlockSize != 0)
        {
            --end;
            min = Math.Min(min, _minimum[channel][end]);
            max = Math.Max(max, _maximum[channel][end]);
        }

        int left = _leafCount + first / BlockSize, right = _leafCount + end / BlockSize;
        var lo = _blockMinimum[channel];
        var hi = _blockMaximum[channel];
        while (left < right)
        {
            if ((left & 1) != 0)
            {
                min = Math.Min(min, lo[left]);
                max = Math.Max(max, hi[left++]);
            }

            if ((right & 1) != 0)
            {
                --right;
                min = Math.Min(min, lo[right]);
                max = Math.Max(max, hi[right]);
            }

            left /= 2;
            right /= 2;
        }
    }

    /// <summary>
    ///     音声ファイルを読み込み、保持するビン数が上限を超えない密度で波形データを生成します。
    /// </summary>
    /// <param name="path">音声ファイルのパス。</param>
    /// <param name="cancellation">読み込みのキャンセル トークン。</param>
    /// <param name="binsPerSecond">要求する 1 秒あたりのビン数。</param>
    /// <returns>生成した波形データ。</returns>
    public static WaveformData Load(string path, CancellationToken cancellation = default,
        int binsPerSecond = DefaultBinsPerSecond)
    {
        using var reader = new AudioFileReader(path);
        // Limit retained waveform bins, not the duration of the audio.
        const int maximumBins = 2_000_000;
        if (reader.TotalTime.TotalSeconds > 0)
            binsPerSecond = Math.Min(binsPerSecond,
                Math.Max(1, (int)Math.Floor(maximumBins / reader.TotalTime.TotalSeconds)));
        return Read(reader, cancellation, binsPerSecond);
    }

    /// <summary>
    ///     サンプル プロバイダーから全サンプルを読み取り、チャンネル・ビンごとの最小値と最大値を集計します。
    /// </summary>
    /// <param name="source">読み取り元のサンプル プロバイダー。</param>
    /// <param name="cancellation">読み取りのキャンセル トークン。</param>
    /// <param name="binsPerSecond">要求する 1 秒あたりのビン数 (サンプリングレートを上限とします)。</param>
    /// <returns>生成した波形データ。</returns>
    public static WaveformData Read(ISampleProvider source, CancellationToken cancellation = default,
        int binsPerSecond = DefaultBinsPerSecond)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        int rate = source.WaveFormat.SampleRate, channels = source.WaveFormat.Channels;
        if (binsPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(binsPerSecond));
        if (rate < 1 || channels < 1) throw new ArgumentException(Strings.InvalidAudioFormat, nameof(source));
        // Preserve every sample when the source rate is lower than the requested density.
        binsPerSecond = Math.Min(binsPerSecond, rate);
        var min = new List<float>[channels];
        var max = new List<float>[channels];
        for (var ch = 0; ch < channels; ch++)
        {
            min[ch] = new List<float>();
            max[ch] = new List<float>();
        }

        var buffer = new float[4096 * channels];
        long samples = 0;
        int count;
        cancellation.ThrowIfCancellationRequested();
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var i = 0; i < count; i++, samples++)
            {
                var frame = samples / channels;
                int ch = (int)(samples % channels), bin = (int)(frame * binsPerSecond / rate);
                while (min[ch].Count <= bin)
                {
                    min[ch].Add(float.PositiveInfinity);
                    max[ch].Add(float.NegativeInfinity);
                }

                var value = buffer[i];
                if (!float.IsFinite(value)) continue;
                min[ch][bin] = Math.Min(min[ch][bin], value);
                max[ch][bin] = Math.Max(max[ch][bin], value);
            }
        }

        if (samples % channels != 0) throw new InvalidDataException("Incomplete audio frame.");
        var minima = new float[channels][];
        var maxima = new float[channels][];
        for (var ch = 0; ch < channels; ch++)
        {
            minima[ch] = min[ch].ToArray();
            maxima[ch] = max[ch].ToArray();
            for (var i = 0; i < minima[ch].Length; i++)
            {
                if (!float.IsFinite(minima[ch][i])) minima[ch][i] = 0;
                if (!float.IsFinite(maxima[ch][i])) maxima[ch][i] = 0;
            }
        }

        return new WaveformData(minima, maxima, rate, binsPerSecond, samples / channels, cancellation);
    }
}