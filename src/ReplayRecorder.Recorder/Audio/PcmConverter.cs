using NAudio.Dsp;
using NAudio.Wave;

namespace ReplayRecorder.Recorder.Audio;

/// <summary>Converts any WASAPI mix format to interleaved 48 kHz float with N channels.</summary>
sealed class PcmConverter
{
    public const int Rate = 48000;

    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    readonly WaveFormat _fmt;
    readonly int _outCh;
    readonly WdlResampler? _resampler;
    float[] _frames = Array.Empty<float>();
    float[] _out = Array.Empty<float>();

    public PcmConverter(WaveFormat fmt, int outChannels)
    {
        _fmt = fmt;
        _outCh = outChannels;
        if (fmt.SampleRate != Rate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(fmt.SampleRate, Rate);
        }
    }

    public ReadOnlySpan<float> Convert(byte[] buf, int bytes)
    {
        int inCh = _fmt.Channels;
        int bps = _fmt.BitsPerSample / 8;
        int frames = bytes / (bps * inCh);
        if (_frames.Length < frames * _outCh) _frames = new float[frames * _outCh];

        bool isFloat = _fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
            (_fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);

        for (int f = 0; f < frames; f++)
        {
            int o = f * bps * inCh;
            float Read(int c)
            {
                int p = o + c * bps;
                if (isFloat && bps == 4) return BitConverter.ToSingle(buf, p);
                return bps switch
                {
                    2 => BitConverter.ToInt16(buf, p) / 32768f,
                    3 => ((buf[p] << 8 | buf[p + 1] << 16 | buf[p + 2] << 24) >> 8) / 8388608f,
                    4 => BitConverter.ToInt32(buf, p) / 2147483648f,
                    _ => 0f,
                };
            }

            if (_outCh == 1)
            {
                float s = 0;
                for (int c = 0; c < inCh; c++) s += Read(c);
                _frames[f] = s / inCh;
            }
            else if (inCh == 1)
            {
                _frames[f * 2] = _frames[f * 2 + 1] = Read(0);
            }
            else if (inCh == 2)
            {
                _frames[f * 2] = Read(0);
                _frames[f * 2 + 1] = Read(1);
            }
            else
            {
                // Surround -> stereo (FL FR FC LFE BL BR SL SR), standard -3 dB center/surround fold-down.
                float c3 = Read(2) * 0.707f;
                float l = Read(0) + c3, r = Read(1) + c3;
                if (inCh >= 6) { l += Read(4) * 0.707f; r += Read(5) * 0.707f; }
                if (inCh >= 8) { l += Read(6) * 0.707f; r += Read(7) * 0.707f; }
                _frames[f * 2] = l * 0.5f;
                _frames[f * 2 + 1] = r * 0.5f;
            }
        }

        if (_resampler == null) return _frames.AsSpan(0, frames * _outCh);

        int want = _resampler.ResamplePrepare(frames, _outCh, out var inBuf, out var inOff);
        Array.Copy(_frames, 0, inBuf, inOff, Math.Min(want, frames) * _outCh);
        int maxOut = frames * Rate / _fmt.SampleRate + 64;
        if (_out.Length < maxOut * _outCh) _out = new float[maxOut * _outCh];
        int got = _resampler.ResampleOut(_out, 0, frames, maxOut, _outCh);
        return _out.AsSpan(0, got * _outCh);
    }
}
