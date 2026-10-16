// =====================================================================================
//  DmxFrame.cs
// -------------------------------------------------------------------------------------
//  Builds the binary frames expected by the RY-AI box (protocol rebuilt from
//  ConsoleFragment.java of the Android app, confirmed on the real hardware by the
//  blink test).
//
//  FRAME LAYOUT (521 bytes):
//      [0]        0x00
//      [1]        'K' (0x4B)                 )  start markers, in clear
//      [2]        'T' (0x54)                 )
//      [3]        "scene" byte   (0 = direct manual control)
//      [4]        "speed" byte   (0 in manual control)
//      [5..516]   512 bytes = DMX universe (channel 1 -> byte 5 ... channel 512 -> 516),
//                 scrambled by Cipher.Encrypt
//      [517]      key n1 = sin[codeNum]      (written by Cipher.Encrypt)
//      [518]      key n2 = codeNum           (written by Cipher.Encrypt)
//      [519]      'T' (0x54)                 )  end markers, in clear
//      [520]      'K' (0x4B)                 )
//
//  "UNIVERSE" CONVENTION used everywhere in the project: an array of 513 bytes
//  whose index 0 is unused, and index N = value of DMX channel N (1..512).
//  It avoids off-by-one errors between "channel number" and "array index".
// =====================================================================================

namespace DmxController.Core.Protocol;

/// <summary>Building and reading of the frames of the RY-AI box.</summary>
public static class DmxFrame
{
    /// <summary>Total size of a frame sent to the box.</summary>
    public const int FrameLength = 521;

    /// <summary>Number of channels of a DMX universe.</summary>
    public const int UniverseSize = 512;

    /// <summary>Index, in the frame, of the byte of DMX channel 1.</summary>
    public const int PayloadStart = 5;

    /// <summary>Index where Cipher.Encrypt writes its 2 key bytes.</summary>
    public const int MarkerOffset = 517;

    /// <summary>Handshake sent right after opening the port (SendBTConnectData).</summary>
    public static byte[] HandshakeConnect() => new byte[] { 0xBB, 0x99, 0x77, 0x01, 0x77, 0x99 };

    /// <summary>Handshake sent right before closing the port (SendBTDisConnectData).</summary>
    public static byte[] HandshakeDisconnect() => new byte[] { 0xBB, 0x77, 0x99, 0x10, 0x99, 0x77, 0x00, 0x55 };

    /// <summary>Creates an empty universe (513 bytes, indexes 1..512 used).</summary>
    public static byte[] NewUniverse() => new byte[UniverseSize + 1];

    /// <summary>
    /// Builds the complete (already scrambled) frame from a DMX universe.
    /// </summary>
    /// <param name="universe">513 bytes, index N = DMX channel N.</param>
    /// <param name="codeNum">Counter 0..255; it is written INTO the frame, so the
    /// box does not need to be in sync with our counter.</param>
    public static byte[] Build(byte[] universe, int codeNum)
    {
        if (universe.Length != UniverseSize + 1)
        {
            throw new ArgumentException("L'univers doit faire 513 octets (index 1 a 512).", nameof(universe));
        }

        var frame = new byte[FrameLength];
        frame[0] = 0x00;
        frame[1] = (byte)'K';
        frame[2] = (byte)'T';
        frame[3] = 0; // scene: 0 = direct manual control
        frame[4] = 0; // speed: 0 in manual control
        Array.Copy(universe, 1, frame, PayloadStart, UniverseSize);
        frame[519] = (byte)'T';
        frame[520] = (byte)'K';

        return Cipher.Encrypt(frame, PayloadStart, UniverseSize, codeNum & 0xFF, MarkerOffset);
    }

    /// <summary>
    /// Inverse of <see cref="Build"/>: recovers the universe in clear from a scrambled
    /// frame. Used by the tests and to analyse captures.
    /// </summary>
    public static byte[] DecodeUniverse(byte[] frame)
    {
        if (frame.Length != FrameLength)
        {
            throw new ArgumentException($"Une trame fait {FrameLength} octets.", nameof(frame));
        }

        byte[] clear = Cipher.DecodeFrame(frame, PayloadStart, UniverseSize, MarkerOffset);
        var universe = NewUniverse();
        Array.Copy(clear, PayloadStart, universe, 1, UniverseSize);
        return universe;
    }
}
