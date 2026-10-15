// =====================================================================================
//  DmxLink.cs
// -------------------------------------------------------------------------------------
//  Liaison avec le boitier USB-RDM-DMX Bluetooth :
//    1) ouverture du port COM virtuel (Bluetooth SPP, ex: COM6 "sortant") ;
//    2) envoi du handshake de connexion ;
//    3) boucle d'envoi continue dans un thread dedie : a chaque tour, on demande a
//       la "source" (le moteur de spectacle, IFrameSource) de calculer l'univers DMX,
//       on construit la trame (DmxFrame.Build) et on l'ecrit sur le port ;
//    4) a la fermeture, handshake de deconnexion.
//
//  L'appli Android d'origine envoie une trame toutes les 100 ms (10 Hz) : c'est la
//  valeur par defaut. Une cadence plus rapide donnerait des mouvements plus fluides
//  mais n'a PAS encore ete testee sur le boitier (voir CLAUDE.md, a faire).
// =====================================================================================

using System.Diagnostics;
using System.IO.Ports;

namespace DmxController.Core.Protocol;

/// <summary>
/// Fournisseur de l'univers DMX a envoyer. Le moteur de spectacle (ShowEngine)
/// implemente cette interface.
/// </summary>
public interface IFrameSource
{
    /// <summary>
    /// Remplit l'univers (513 octets, index N = canal DMX N, deja remis a zero).
    /// Appele depuis le thread d'envoi : l'implementation doit etre thread-safe.
    /// </summary>
    void Render(byte[] universe);
}

public sealed class DmxLink : IDisposable
{
    // Protege Connect / Disconnect / Start / Stop (appeles depuis l'IHM ET depuis
    // une tache de reconnexion automatique en arriere-plan).
    private readonly object _stateLock = new();

    private SerialPort? _port;
    private Thread? _thread;
    private volatile bool _sending;
    private volatile int _sendIntervalMs = 100;
    private int _codeNum;
    private long _framesSent;

    /// <summary>Source de l'univers DMX (en pratique : le ShowEngine).</summary>
    public IFrameSource? Source { get; set; }

    /// <summary>
    /// Leve depuis le thread d'envoi quand l'ecriture echoue (Bluetooth coupe,
    /// boitier eteint...). L'abonne doit repasser sur le thread de l'IHM
    /// (BeginInvoke) avant de toucher a l'interface ou d'appeler Disconnect().
    /// </summary>
    public event EventHandler<string>? ConnectionLost;

    /// <summary>Intervalle entre deux trames, en millisecondes (20 a 1000).</summary>
    public int SendIntervalMs
    {
        get => _sendIntervalMs;
        set => _sendIntervalMs = Math.Clamp(value, 20, 1000);
    }

    public bool IsConnected => _port is { IsOpen: true };
    public bool IsSending => _sending;
    public string? PortName => _port?.PortName;
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public string? LastError { get; private set; }

    /// <summary>Liste des ports COM presents sur la machine, tries.</summary>
    public static string[] GetPortNames() =>
        SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)               // COM6 avant COM10
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Ouvre le port et envoie le handshake. Peut bloquer plusieurs secondes le temps
    /// que Windows etablisse la liaison Bluetooth : a appeler hors du thread de l'IHM.
    /// Le debit n'a pas d'importance (port virtuel Bluetooth, pas un vrai UART).
    /// </summary>
    public void Connect(string portName, int baudRate = 9600)
    {
        lock (_stateLock)
        {
            if (IsConnected)
            {
                throw new InvalidOperationException("Deja connecte : deconnecte d'abord.");
            }

            var port = new SerialPort(portName, baudRate) { WriteTimeout = 2000, ReadTimeout = 1000 };
            try
            {
                port.Open();
                byte[] hello = DmxFrame.HandshakeConnect();
                port.Write(hello, 0, hello.Length);
            }
            catch
            {
                port.Dispose();
                throw;
            }

            _port = port;
            LastError = null;
            Interlocked.Exchange(ref _framesSent, 0);
        }
    }

    /// <summary>Demarre la boucle d'envoi continu (thread d'arriere-plan).</summary>
    public void StartSending()
    {
        lock (_stateLock)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Port non ouvert.");
            }
            if (_sending)
            {
                return;
            }

            _sending = true;
            _thread = new Thread(SendLoop) { IsBackground = true, Name = "DmxSendLoop" };
            _thread.Start();
        }
    }

    /// <summary>Arrete la boucle d'envoi (le port reste ouvert).</summary>
    public void StopSending()
    {
        lock (_stateLock)
        {
            _sending = false;
            var thread = _thread;
            // Jamais de Join sur soi-meme (cas ou on serait appele depuis le thread d'envoi).
            if (thread is not null && thread != Thread.CurrentThread)
            {
                thread.Join(1500);
            }
            _thread = null;
        }
    }

    /// <summary>Arrete l'envoi, envoie le handshake de deconnexion et ferme le port.</summary>
    public void Disconnect()
    {
        lock (_stateLock)
        {
            StopSending();

            if (_port is { IsOpen: true })
            {
                try
                {
                    byte[] bye = DmxFrame.HandshakeDisconnect();
                    _port.Write(bye, 0, bye.Length);
                }
                catch (Exception)
                {
                    // Le boitier a peut-etre deja coupe la liaison : on ferme quand meme.
                }

                try { _port.Close(); } catch (Exception) { /* idem */ }
            }

            _port?.Dispose();
            _port = null;
        }
    }

    private int NextCodeNum()
    {
        _codeNum = (_codeNum + 1) & 0xFF;
        return _codeNum;
    }

    private void SendLoop()
    {
        var universe = DmxFrame.NewUniverse();
        var clock = Stopwatch.StartNew();

        while (_sending)
        {
            long started = clock.ElapsedMilliseconds;
            try
            {
                Array.Clear(universe);
                Source?.Render(universe);

                byte[] frame = DmxFrame.Build(universe, NextCodeNum());
                var port = _port;
                if (port is null || !port.IsOpen)
                {
                    throw new IOException("Le port serie a ete ferme.");
                }

                port.Write(frame, 0, frame.Length);
                Interlocked.Increment(ref _framesSent);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _sending = false;
                try
                {
                    ConnectionLost?.Invoke(this, ex.Message);
                }
                catch (Exception)
                {
                    // Un abonne defaillant (fenetre deja fermee...) ne doit pas faire
                    // planter tout le processus depuis ce thread.
                }
                return;
            }

            // On vise une cadence reguliere : on retire le temps deja passe a calculer.
            long elapsed = clock.ElapsedMilliseconds - started;
            Thread.Sleep((int)Math.Max(1, _sendIntervalMs - elapsed));
        }
    }

    public void Dispose() => Disconnect();
}
