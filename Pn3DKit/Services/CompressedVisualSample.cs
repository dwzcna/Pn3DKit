using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Pn3DKit.Models;

namespace Pn3DKit.Services;

/// <summary>代表图像立即无损压缩，长期只保存压缩数据；回读完整像素后再判断匹配。</summary>
public sealed class CompressedVisualSample
{
    private readonly VisualSample _metadata;
    private readonly byte[] _light, _dark;
    private readonly byte[]? _weight;
    /// <summary>哈希只用于候选分桶，相同哈希仍须解压并完整比较。</summary>
    public string Fingerprint { get; }
    /// <summary>长期保留的压缩像素总长度，供验证内存行为使用。</summary>
    public int CompressedBytes => checked(_light.Length + _dark.Length + (_weight?.Length ?? 0));

    /// <summary>复制外观元数据并压缩三条通道，不保留调用方的原始数组。</summary>
    public CompressedVisualSample(VisualSample sample) : this(sample, FingerprintOf(sample)) { }

    /// <summary>调用方已计算摘要时复用；仍保存独立元数据和无损压缩像素。</summary>
    internal CompressedVisualSample(VisualSample sample, string fingerprint)
    {
        _metadata = sample with { LightPixels = [], DarkPixels = [], WeightCodePixels = null,
            EffectiveColors = sample.EffectiveColors.ToArray(), EffectiveLineWeights = sample.EffectiveLineWeights.ToArray() };
        _light = Compress(sample.LightPixels); _dark = Compress(sample.DarkPixels);
        _weight = sample.WeightCodePixels == null ? null : Compress(sample.WeightCodePixels);
        Fingerprint = fingerprint;
    }

    /// <summary>摘要只负责分桶；相同摘要仍比较全部像素和外观。</summary>
    public static string FingerprintOf(VisualSample sample)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { sample.Frame, sample.EffectiveColors, sample.EffectiveLineWeights }));
        hash.AppendData(sample.LightPixels); hash.AppendData(sample.DarkPixels);
        if (sample.WeightCodePixels != null) hash.AppendData(sample.WeightCodePixels);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>用固定大小解压缓冲核对完整像素，不为匹配来源展开整张代表图。</summary>
    internal bool Matches(VisualSample sample)
    {
        if (_metadata.Frame != sample.Frame
            || !sample.EffectiveColors.AsSpan().SequenceEqual(_metadata.EffectiveColors)
            || !sample.EffectiveLineWeights.AsSpan().SequenceEqual(_metadata.EffectiveLineWeights)) return false;
        return SamePixels(_light, sample.LightPixels) && SamePixels(_dark, sample.DarkPixels)
            && (_metadata.EffectiveLineWeights.Length <= 1
                || _weight != null && sample.WeightCodePixels != null && SamePixels(_weight, sample.WeightCodePixels));
    }

    /// <summary>解压后逐字节比较，不使用差异比例或哈希代替内容核对。</summary>
    private static bool SamePixels(byte[] compressed, byte[] actual)
    {
        using var input = new MemoryStream(compressed, false);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int offset = 0;
            while (offset < actual.Length)
            {
                int size = Math.Min(65536, actual.Length - offset);
                zip.ReadExactly(buffer.AsSpan(0, size));
                if (!buffer.AsSpan(0, size).SequenceEqual(actual.AsSpan(offset, size))) return false;
                offset += size;
            }
            if (zip.ReadByte() != -1) throw new InvalidDataException("代表图像长度与当前样本不符。");
            return true;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    /// <summary>仅在候选碰撞和差异输出时回读完整数据，返回独立数组。</summary>
    public VisualSample Read() => _metadata with { LightPixels = Expand(_light), DarkPixels = Expand(_dark),
        WeightCodePixels = _weight == null ? null : Expand(_weight),
        EffectiveColors = _metadata.EffectiveColors.ToArray(), EffectiveLineWeights = _metadata.EffectiveLineWeights.ToArray() };

    /// <summary>采用无损 Deflate，不量化颜色或线宽编码。</summary>
    private static byte[] Compress(byte[] pixels)
    {
        using var stream = new MemoryStream();
        using (var zip = new DeflateStream(stream, CompressionLevel.Fastest, true)) zip.Write(pixels);
        return stream.ToArray();
    }

    /// <summary>限制最大解压长度，损坏数据不能引发无界内存分配。</summary>
    private byte[] Expand(byte[] compressed)
    {
        int required = checked(_metadata.Frame.Pixels * _metadata.Frame.Pixels * 4);
        using var input = new MemoryStream(compressed, false);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        var pixels = new byte[required];
        zip.ReadExactly(pixels);
        if (zip.ReadByte() != -1) throw new InvalidDataException("图像解压长度与公共视窗不符。");
        return pixels;
    }
}
