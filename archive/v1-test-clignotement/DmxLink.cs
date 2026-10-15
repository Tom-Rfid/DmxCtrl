// =====================================================================================
//  DmxLink.cs
// -------------------------------------------------------------------------------------
//  Gere la connexion au boitier USB-RDM-DMX Bluetooth et l'envoi continu des 512
//  canaux DMX, en reproduisant exactement ce que fait l'appli Android RY-AI :
//
//    1) Le boitier est associe en Bluetooth classique (profil SPP). Windows cree
//       alors un port COM virtuel (ex: COM6, "sortant") qu'on ouvre comme un port
//       serie tout a fait normal avec System.IO.Ports.SerialPort.
//
//    2) Juste apres l'ouverture, on envoie une petite trame fixe de "handshake"
//       pour signaler au boitier qu'on est bien connecte (sinon il ignore tout).
//
//    3) Ensuite, TOUT LE TEMPS qu'on veut piloter les appareils, on envoie une
//       trame de 521 octets toutes les ~100 ms (10 fois par seconde), meme si
//       rien n'a change : c'est le "rafraichissement DMX", exactement comme le
//       ferait un vrai controleur DMX filaire. Si on arrete d'envoyer, le
//       boitier (ou les lyres) peuvent repasser a une valeur par defaut.
//
//  STRUCTURE EXACTE D'UNE TRAME (521 octets), reconstituee depuis ConsoleFragment.java :
//
//      [0]        0x00
//      [1]        'K' (0x4B)                 )  marqueurs de debut, en clair
//      [2]        'T' (0x54)                 )
//      [3]        octet "scene" (0 = pilotage manuel direct, pas une scene memorisee)
//      [4]        octet "vitesse" (0 en mode manuel)
//      [5..516]   512 octets = univers DMX complet (canal 1 a l'indice 5, canal 512
//                 a l'indice 516), APRES brouillage par Cipher.Encrypt()
//      [517]      octet de cle n[1] (ecrit automatiquement par Cipher.Encrypt)
//      [518]      octet de cle n[2] (= le compteur codeNum utilise pour cette trame)
//      [519]      'T' (0x54)                 )  marqueurs de fin, en clair
//      [520]      'K' (0x4B)                 )
//
//  MAPPING DES CANAUX (d'apres la configuration DMX convenue precedemment) :
//      - Lyre Chauvet Intimidator Spot 260X n#1 : adresse DMX 1,  canaux  1-14
//      - Lyre Chauvet Intimidator Spot 260X n#2 : adresse DMX 15, canaux 15-28
//      - Mindi Spider 8x3W RGBW                 : adresse DMX 29, canaux 29-43
//
//      ATTENTION : on connait l'ADRESSAGE (confirme avec toi precedemment), mais
//      PAS encore la fonction precise de chaque canal (lequel est Pan, Tilt,
//      couleur, etc.) pour la 260X. Tant que ce n'est pas verifie avec le vrai
//      manuel Chauvet, l'IHM affiche des sliders generiques "Canal 1, Canal 2...".
//      Voir PROTOCOLE.md pour les notes a ce sujet.
// =====================================================================================

using System.IO.Ports;

namespace DmxController;

public sealed class DmxLink : IDisposable
{
    // Les deux poignees de main (handshake) trouvees dans ConsoleFragment.java :
    //   SendBTConnectData()    -> envoyee juste apres l'ouverture du port
    //   SendBTDisConnectData() -> envoyee juste avant de fermer
    private static readonly byte[] HandshakeConnect = { 0xBB, 0x99, 0x77, 0x01, 0x77, 0x99 };
    private static readonly byte[] HandshakeDisconnect = { 0xBB, 0x77, 0x99, 0x10, 0x99, 0x77, 0x00, 0x55 };

    private const int FrameLength = 521;      // taille totale d'une trame DMX
    private const int DmxUniverseSize = 512;  // nombre de canaux DMX standard
    private const int PayloadStart = 5;       // le canal 1 commence a l'indice 5
    private const int MarkerOffset = 517;     // ou Cipher.Encrypt ecrit ses 2 octets de cle

    private SerialPort? _port;
    private Thread? _senderThread;
    private volatile bool _sending;    // true = la boucle d'envoi 10 Hz tourne
    private int _codeNum;              // compteur 0..255, incremente a chaque trame

    // Buffer des 512 canaux DMX. On utilise les index 1..512 (comme en DMX reel,
    // ou le "canal 0" n'existe pas) pour coller exactement au vocabulaire habituel.
    private readonly byte[] _channels = new byte[DmxUniverseSize + 1];
    private readonly object _channelsLock = new();

    /// <summary>True si le port serie est actuellement ouvert.</summary>
    public bool IsConnected => _port is { IsOpen: true };

    /// <summary>
    /// Ouvre le port COM indique et envoie la trame de handshake de connexion.
    /// Le debit (baudRate) n'a pas vraiment d'importance ici : le port COM est
    /// virtuel (cree par le pilote Bluetooth SPP de Windows), il ne pilote pas
    /// un vrai UART. 9600 est la valeur utilisee par defaut, qui fonctionne.
    /// </summary>
    public void Connect(string portName, int baudRate = 9600)
    {
        if (IsConnected)
        {
            throw new InvalidOperationException("Deja connecte - deconnecte d'abord.");
        }

        _port = new SerialPort(portName, baudRate)
        {
            // Le boitier n'utilise pas les signaux RTS/CTS/DTR classiques ; on
            // laisse les valeurs par defaut de SerialPort qui conviennent.
            WriteTimeout = 1000,
            ReadTimeout = 1000,
        };
        _port.Open();

        _port.Write(HandshakeConnect, 0, HandshakeConnect.Length);
    }

    /// <summary>
    /// Arrete la boucle d'envoi si elle tourne, envoie la trame de handshake de
    /// deconnexion, puis ferme le port serie.
    /// </summary>
    public void Disconnect()
    {
        StopSending();

        if (_port is { IsOpen: true })
        {
            try
            {
                _port.Write(HandshakeDisconnect, 0, HandshakeDisconnect.Length);
            }
            catch (Exception)
            {
                // Si le boitier s'est deja deconnecte de son cote, l'ecriture
                // peut echouer : ce n'est pas grave, on ferme quand meme.
            }

            _port.Close();
        }

        _port?.Dispose();
        _port = null;
    }

    /// <summary>
    /// Modifie la valeur d'un canal DMX (1 a 512). Thread-safe : peut etre
    /// appele depuis le thread de l'interface (quand on bouge un slider) alors
    /// que la boucle d'envoi tourne dans son propre thread en arriere-plan.
    /// </summary>
    public void SetChannel(int channel, byte value)
    {
        if (channel < 1 || channel > DmxUniverseSize)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "Un canal DMX va de 1 a 512.");
        }

        lock (_channelsLock)
        {
            _channels[channel] = value;
        }
    }

    /// <summary>Lit la valeur actuelle d'un canal (utile pour rafraichir l'IHM).</summary>
    public byte GetChannel(int channel)
    {
        lock (_channelsLock)
        {
            return _channels[channel];
        }
    }

    /// <summary>Remet tous les canaux a 0 (coupe toutes les lyres / le Spider).</summary>
    public void Blackout()
    {
        lock (_channelsLock)
        {
            Array.Clear(_channels, 0, _channels.Length);
        }
    }

    /// <summary>
    /// Demarre la boucle d'envoi continu (10 Hz / une trame toutes les 100 ms),
    /// dans un thread dedie, pour ne jamais bloquer l'interface graphique.
    /// </summary>
    public void StartSending()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Connecte-toi au port COM avant de lancer l'envoi.");
        }

        if (_sending)
        {
            return; // deja en cours
        }

        _sending = true;
        _senderThread = new Thread(SendLoop)
        {
            IsBackground = true,   // le thread ne doit pas empecher l'appli de se fermer
            Name = "DmxSenderLoop",
        };
        _senderThread.Start();
    }

    /// <summary>Arrete la boucle d'envoi continu (sans fermer le port).</summary>
    public void StopSending()
    {
        _sending = false;
        _senderThread?.Join(500); // on laisse au thread 500 ms pour se terminer proprement
        _senderThread = null;
    }

    /// <summary>
    /// Boucle executee dans le thread d'arriere-plan : construit une trame a partir
    /// de l'etat courant des 512 canaux, la brouille, l'envoie, attend 100 ms, recommence.
    /// </summary>
    private void SendLoop()
    {
        while (_sending)
        {
            try
            {
                SendOneFrame();
            }
            catch (Exception)
            {
                // Une erreur d'ecriture (cable/BT coupe, etc.) ne doit pas planter
                // le thread silencieusement ; on arrete juste l'envoi.
                _sending = false;
                break;
            }

            Thread.Sleep(100); // ~10 Hz, comme dans l'appli Android d'origine
        }
    }

    /// <summary>
    /// Construit et envoie UNE trame DMX de 521 octets a partir de l'etat courant
    /// des canaux. Methode publique en plus de la boucle automatique, pour pouvoir
    /// par exemple forcer l'envoi immediat d'un changement depuis l'IHM.
    /// </summary>
    public void SendOneFrame()
    {
        if (_port is not { IsOpen: true })
        {
            return;
        }

        byte[] frame = new byte[FrameLength];
        frame[0] = 0x00;
        frame[1] = (byte)'K';
        frame[2] = (byte)'T';
        frame[3] = 0; // scene = 0 -> pilotage manuel direct (pas une scene memorisee)
        frame[4] = 0; // vitesse = 0 en mode manuel

        lock (_channelsLock)
        {
            // Canal DMX "n" (1..512) va a l'indice (PayloadStart - 1 + n) = (4 + n).
            Array.Copy(_channels, 1, frame, PayloadStart, DmxUniverseSize);
        }

        frame[519] = (byte)'T';
        frame[520] = (byte)'K';

        // Le compteur boucle de 0 a 255 ; peu importe la valeur de depart, car le
        // boitier relit le codeNum ECRIT DANS la trame elle-meme pour la debrouiller
        // (voir Cipher.Encrypt / Cipher.DecodeFrame) : pas besoin de synchronisation
        // particuliere avec l'etat interne de l'appli Android.
        _codeNum = (_codeNum + 1) & 0xFF;

        byte[] encoded = Cipher.Encrypt(frame, PayloadStart, DmxUniverseSize, _codeNum, MarkerOffset);

        _port.Write(encoded, 0, encoded.Length);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
