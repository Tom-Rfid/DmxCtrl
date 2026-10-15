// Tests du protocole : la trame C# doit etre identique, octet pour octet, a celle du
// script Python tools/cipher.py (valide sur le vrai boitier par le test de clignotement).

using DmxController.Core.Protocol;

namespace DmxController.Tests;

public class ProtocolTests
{
    // Genere par tools/gen_reference_frame.py : canal 1=255, 2=128, 3=1, 14=42, 512=7, codeNum=1.
    private const string PythonReferenceHex = "004b540000bde0e47f7f7f7f7f7f7f7f7f7f0a7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f188201544b";

    private static byte[] ReferenceUniverse()
    {
        var universe = DmxFrame.NewUniverse();
        universe[1] = 255;
        universe[2] = 128;
        universe[3] = 1;
        universe[14] = 42;
        universe[512] = 7;
        return universe;
    }

    [Fact]
    public void Build_matches_python_reference_byte_for_byte()
    {
        byte[] frame = DmxFrame.Build(ReferenceUniverse(), codeNum: 1);
        Assert.Equal(PythonReferenceHex, Convert.ToHexStringLower(frame));
    }

    [Fact]
    public void Frame_has_clear_markers_and_key_bytes()
    {
        byte[] frame = DmxFrame.Build(ReferenceUniverse(), codeNum: 42);
        Assert.Equal(DmxFrame.FrameLength, frame.Length);
        Assert.Equal(new byte[] { 0x00, (byte)'K', (byte)'T', 0, 0 }, frame[..5]);
        Assert.Equal((byte)'T', frame[519]);
        Assert.Equal((byte)'K', frame[520]);
        Assert.Equal(42, frame[518]); // codeNum ecrit en clair dans la trame
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(255)]
    public void Build_then_decode_gives_back_the_universe(int codeNum)
    {
        var universe = DmxFrame.NewUniverse();
        var random = new Random(codeNum);
        for (int ch = 1; ch <= 512; ch++)
        {
            universe[ch] = (byte)random.Next(256);
        }

        byte[] decoded = DmxFrame.DecodeUniverse(DmxFrame.Build(universe, codeNum));
        Assert.Equal(universe[1..], decoded[1..]);
    }

    [Fact]
    public void Handshakes_are_the_ones_from_the_android_app()
    {
        Assert.Equal(new byte[] { 0xBB, 0x99, 0x77, 0x01, 0x77, 0x99 }, DmxFrame.HandshakeConnect());
        Assert.Equal(new byte[] { 0xBB, 0x77, 0x99, 0x10, 0x99, 0x77, 0x00, 0x55 }, DmxFrame.HandshakeDisconnect());
    }
}
