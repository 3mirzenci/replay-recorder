namespace ReplayRecorder.Recorder;

/// <summary>Reads the bits of a 188-byte MPEG-TS packet that the RAM buffer needs.</summary>
static class TsPacket
{
    /// <summary>PTS in seconds if this packet starts a keyframe's PES, else null.</summary>
    public static double? KeyframePts(ReadOnlySpan<byte> pkt)
    {
        if ((pkt[1] & 0x40) == 0) return null;          // payload_unit_start
        int afc = (pkt[3] >> 4) & 3, off = 4;
        bool rai = false;
        if ((afc & 2) != 0)
        {
            int len = pkt[4];
            if (len > 0) rai = (pkt[5] & 0x40) != 0;    // random_access_indicator
            off = 5 + len;
        }
        if (!rai || (afc & 1) == 0 || off + 14 > 188) return null;
        if (pkt[off] != 0 || pkt[off + 1] != 0 || pkt[off + 2] != 1 || (pkt[off + 7] & 0x80) == 0) return null;
        long pts = ((long)(pkt[off + 9] >> 1) & 7) << 30 | (long)pkt[off + 10] << 22 |
                   (long)(pkt[off + 11] >> 1) << 15 | (long)pkt[off + 12] << 7 | (long)(pkt[off + 13] >> 1);
        return pts / 90000.0; // the timeline starts at 0, so the 33-bit wrap (26.5 h) never comes up in practice
    }
}
