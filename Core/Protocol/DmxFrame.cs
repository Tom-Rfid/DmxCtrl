// =====================================================================================
//  DmxFrame.cs
// -------------------------------------------------------------------------------------
//  Construction des trames binaires attendues par le boitier RY-AI (protocole
//  reconstitue depuis ConsoleFragment.java de l'appli Android, confirme sur le
//  vrai materiel par le test de clignotement).
//
//  STRUCTURE D'UNE TRAME (521 octets) :
//      [0]        0x00
//      [1]        'K' (0x4B)                 )  marqueurs de debut, en clair
//      [2]        'T' (0x54)                 )
//      [3]        octet "scene"   (0 = pilotage manuel direct)
//      [4]        octet "vitesse" (0 en pilotage manuel)
//      [5..516]   512 octets = univers DMX (canal 1 -> octet 5 ... canal 512 -> 516),
//                 brouilles par Cipher.Encrypt
//      [517]      cle n1 = sin[codeNum]      (ecrite par Cipher.Encrypt)
//      [518]      cle n2 = codeNum           (ecrite par Cipher.Encrypt)
//      [519]      'T' (0x54)                 )  marqueurs de fin, en clair
//      [520]      'K' (0x4B)                 )
//
//  CONVENTION "UNIVERS" utilisee partout dans le projet : un tableau de 513 octets
//  dont l'index 0 est inutilise, et l'index N = valeur du canal DMX N (1..512).
//  Ca evite les decalages de 1 entre "numero de canal" et "index de tableau".
// =====================================================================================

namespace DmxController.Core.Protocol;

public static class DmxFrame
{
    /// <summary>Taille totale d'une trame envoyee au boitier.</summary>
    public const int FrameLength = 521;

    /// <summary>Nombre de canaux d'un univers DMX.</summary>
    public const int UniverseSize = 512;

    /// <summary>Indice, dans la trame, de l'octet du canal DMX 1.</summary>
    public const int PayloadStart = 5;

    /// <summary>Indice ou Cipher.Encrypt ecrit ses 2 octets de cle.</summary>
    public const int MarkerOffset = 517;

    /// <summary>Handshake envoye juste apres l'ouverture du port (SendBTConnectData).</summary>
    public static byte[] HandshakeConnect() => new byte[] { 0xBB, 0x99, 0x77, 0x01, 0x77, 0x99 };

    /// <summary>Handshake envoye juste avant la fermeture du port (SendBTDisConnectData).</summary>
    public static byte[] HandshakeDisconnect() => new byte[] { 0xBB, 0x77, 0x99, 0x10, 0x99, 0x77, 0x00, 0x55 };

    /// <summary>Cree un univers vide (513 octets, index 1..512 utilises).</summary>
    public static byte[] NewUniverse() => new byte[UniverseSize + 1];

    /// <summary>
    /// Construit la trame complete (deja brouillee) a partir d'un univers DMX.
    /// </summary>
    /// <param name="universe">513 octets, index N = canal DMX N.</param>
    /// <param name="codeNum">Compteur 0..255 ; il est ecrit DANS la trame, donc le
    /// boitier n'a pas besoin d'etre synchronise avec notre compteur.</param>
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
        frame[3] = 0; // scene : 0 = pilotage manuel direct
        frame[4] = 0; // vitesse : 0 en pilotage manuel
        Array.Copy(universe, 1, frame, PayloadStart, UniverseSize);
        frame[519] = (byte)'T';
        frame[520] = (byte)'K';

        return Cipher.Encrypt(frame, PayloadStart, UniverseSize, codeNum & 0xFF, MarkerOffset);
    }

    /// <summary>
    /// Operation inverse de <see cref="Build"/> : retrouve l'univers en clair a partir
    /// d'une trame brouillee. Sert aux tests et a l'analyse de captures.
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
