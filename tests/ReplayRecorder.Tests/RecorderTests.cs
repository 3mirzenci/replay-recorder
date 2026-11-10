using NAudio.Wave;
using ReplayRecorder.Recorder;
using ReplayRecorder.Recorder.Audio;

namespace ReplayRecorder.Tests;

public class PcmConverterTests
{
    static byte[] Int24(params int[] samples) =>
        samples.SelectMany(s => new[] { (byte)s, (byte)(s >> 8), (byte)(s >> 16) }).ToArray();

    [Fact]
    public void Int24StereoKeepsLevelsAndSign()
    {
        var c = new PcmConverter(new WaveFormat(48000, 24, 2), 2);
        var o = c.Convert(Int24(4194304, -4194304), 6).ToArray(); // +0.5, -0.5
        Assert.Equal(new[] { 0.5f, -0.5f }, o);
    }

    [Fact]
    public void Int16StereoToMonoAverages()
    {
        var c = new PcmConverter(new WaveFormat(48000, 16, 2), 1);
        var bytes = new byte[4];
        BitConverter.GetBytes((short)16384).CopyTo(bytes, 0); // 0.5
        BitConverter.GetBytes((short)0).CopyTo(bytes, 2);
        Assert.Equal(new[] { 0.25f }, c.Convert(bytes, 4).ToArray());
    }

    [Fact]
    public void FloatSurroundFoldsCenterIntoBothSides()
    {
        var c = new PcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 6), 2);
        var frame = new float[] { 0, 0, 1, 0, 0, 0 }; // centre only
        var bytes = new byte[24];
        Buffer.BlockCopy(frame, 0, bytes, 0, 24);
        var o = c.Convert(bytes, 24).ToArray();
        Assert.Equal(o[0], o[1]);
        Assert.InRange(o[0], 0.35f, 0.36f); // 0.707 * 0.5
    }

    [Fact]
    public void Resamples44100To48000()
    {
        var c = new PcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), 2);
        var bytes = new byte[4410 * 8]; // 100 ms of silence
        int total = 0;
        for (int i = 0; i < 10; i++) total += c.Convert(bytes, bytes.Length).Length / 2;
        Assert.InRange(total, 47000, 48000); // 1 s in, ~1 s out (the filter holds back a few samples)
    }
}

public class SegmentListTests
{
    [Fact]
    public void ReadsNamesWithCommasAndSkipsBrokenLines()
    {
        var text = "seg_00000.ts,0.000000,2.000000\r\n\"seg,01.ts\",2.000000,4.016667\nbroken line\nseg_00002.ts,abc,6.0\n";
        Assert.Equal(new[] { ("seg_00000.ts", 0.0, 2.0), ("seg,01.ts", 2.0, 4.016667) }, SegmentList.Parse(text));
    }
}

public class TsPacketTests
{
    /// <summary>A video packet that starts a PES, with the given adaptation flags and PTS.</summary>
    static byte[] Packet(bool randomAccess, long pts)
    {
        var p = new byte[188];
        p[0] = 0x47; p[1] = 0x41; p[2] = 0x00; // payload_unit_start, PID 0x100
        p[3] = 0x30;                          // adaptation field + payload
        p[4] = 1;                             // adaptation length
        p[5] = randomAccess ? (byte)0x40 : (byte)0;
        int o = 6;
        p[o] = 0; p[o + 1] = 0; p[o + 2] = 1; p[o + 3] = 0xE0; // PES start, video stream
        p[o + 7] = 0x80;                                        // PTS present
        p[o + 9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        p[o + 10] = (byte)(pts >> 22);
        p[o + 11] = (byte)(0x01 | ((pts >> 14) & 0xFE));
        p[o + 12] = (byte)(pts >> 7);
        p[o + 13] = (byte)(0x01 | ((pts << 1) & 0xFE));
        return p;
    }

    [Fact]
    public void ReadsKeyframeTime() => Assert.Equal(12.5, TsPacket.KeyframePts(Packet(true, 1_125_000)));

    [Fact]
    public void IgnoresNonKeyframes() => Assert.Null(TsPacket.KeyframePts(Packet(false, 90000)));

    [Fact]
    public void ReadsLargeTimes() => Assert.Equal(3600.0, TsPacket.KeyframePts(Packet(true, 3600L * 90000)));
}
