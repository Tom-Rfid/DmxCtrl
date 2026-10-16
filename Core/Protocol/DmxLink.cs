// =====================================================================================
//  DmxLink.cs
// -------------------------------------------------------------------------------------
//  Link with the USB-RDM-DMX Bluetooth box:
//    1) opens the virtual COM port (Bluetooth SPP, e.g. "outgoing" COM6);
//    2) sends the connection handshake;
//    3) continuous send loop in a dedicated thread: on each turn, the "source" (the
//       show engine, IFrameSource) is asked to compute the DMX universe, the frame
//       is built (DmxFrame.Build) and written to the port;
//    4) on closing, disconnection handshake.
//
//  The original Android app sends one frame every 100 ms (10 Hz): that is the
//  default value. A faster rate would give smoother movements but has NOT been
//  tested on the box yet (see CLAUDE.md, to do).
// =====================================================================================

using System.Diagnostics;
using System.IO.Ports;

namespace DmxController.Core.Protocol;

/// <summary>
/// Provider of the DMX universe to send. The show engine (ShowEngine) implements
/// this interface.
/// </summary>
public interface IFrameSource
{
    /// <summary>
    /// Fills the universe (513 bytes, index N = DMX channel N, already cleared).
    /// Called from the send thread: the implementation must be thread-safe.
    /// </summary>
    void Render(byte[] universe);
}

/// <summary>Bluetooth serial link with the box and continuous send loop.</summary>
public sealed class DmxLink : IDisposable
{
    // Protects Connect / Disconnect / Start / Stop (called from the UI AND from an
    // automatic reconnection task in the background).
    private readonly object _stateLock = new();

    private SerialPort? _port;
    private Thread? _thread;
    private volatile bool _sending;
    private volatile int _sendIntervalMs = 100;
    private int _codeNum;
    private long _framesSent;

    /// <summary>Source of the DMX universe (in practice: the ShowEngine).</summary>
    public IFrameSource? Source { get; set; }

    /// <summary>
    /// Raised from the send thread when writing fails (Bluetooth dropped, box switched
    /// off...). The subscriber must switch back to the UI thread (BeginInvoke) before
    /// touching the interface or calling Disconnect().
    /// </summary>
    public event EventHandler<string>? ConnectionLost;

    /// <summary>Interval between two frames, in milliseconds (20 to 1000).</summary>
    public int SendIntervalMs
    {
        get => _sendIntervalMs;
        set => _sendIntervalMs = Math.Clamp(value, 20, 1000);
    }

    /// <summary>true while the COM port is open.</summary>
    public bool IsConnected => _port is { IsOpen: true };
    /// <summary>true while the send loop runs.</summary>
    public bool IsSending => _sending;
    /// <summary>Name of the open port (e.g. "COM6").</summary>
    public string? PortName => _port?.PortName;
    /// <summary>Number of frames sent since the connection.</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);
    /// <summary>Message of the last connection or sending error.</summary>
    public string? LastError { get; private set; }

    /// <summary>List of the COM ports present on the machine, sorted.</summary>
    public static string[] GetPortNames() =>
        SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)               // COM6 before COM10
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Opens the port and sends the handshake. Can block for several seconds while Windows
    /// sets up the Bluetooth link: call it outside the UI thread.
    /// The baud rate does not matter (virtual Bluetooth port, not a real UART).
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

                // Known .NET issue with Bluetooth COM ports: when the link dies, the "finalizer" of
                // the serial stream throws an IOException ("semaphore timeout period has expired")
                // on an internal thread, which would crash the app. It is disabled: SafeClose()
                // closes the port and swallows these errors.
                GC.SuppressFinalize(port.BaseStream);

                byte[] hello = DmxFrame.HandshakeConnect();
                port.Write(hello, 0, hello.Length);
            }
            catch
            {
                SafeClose(port);
                throw;
            }

            _port = port;
            LastError = null;
            Interlocked.Exchange(ref _framesSent, 0);
        }
    }

    /// <summary>
    /// Like Connect + StartSending, but never throws: returns false and the error message
    /// (box off, out of range, port busy...).
    /// </summary>
    public bool TryConnectAndStart(string portName, out string? error)
    {
        try
        {
            Connect(portName);
            StartSending();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Disconnect(); // never throws
            error = FriendlyError(ex);
            LastError = error;
            return false;
        }
    }

    /// <summary>Understandable message for the typical errors of a Bluetooth port.</summary>
    public static string FriendlyError(Exception ex) => ex switch
    {
        IOException io when io.Message.Contains("maphore", StringComparison.OrdinalIgnoreCase) =>
            Tr.T("le boîtier ne répond pas (éteint, hors de portée ou non appairé). ") + io.Message,
        UnauthorizedAccessException => Tr.T("le port est déjà utilisé par un autre programme (appli RY-AI, autre fenêtre…)."),
        TimeoutException => Tr.T("le boîtier ne répond pas (délai dépassé)."),
        _ => ex.Message,
    };

    /// <summary>Closes and releases a port without ever throwing (dead Bluetooth link...).</summary>
    private static void SafeClose(SerialPort port)
    {
        try
        {
            if (port.IsOpen)
            {
                port.BaseStream.Close();
            }
        }
        catch (Exception)
        {
            // link already dead
        }
        try
        {
            port.Dispose();
        }
        catch (Exception)
        {
            // same
        }
    }

    /// <summary>Starts the continuous send loop (background thread).</summary>
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

    /// <summary>Stops the send loop (the port stays open).</summary>
    public void StopSending()
    {
        lock (_stateLock)
        {
            _sending = false;
            var thread = _thread;
            // Never Join itself (in case we are called from the send thread).
            if (thread is not null && thread != Thread.CurrentThread)
            {
                thread.Join(1500);
            }
            _thread = null;
        }
    }

    /// <summary>Stops sending, sends the disconnection handshake and closes the port. Never throws.</summary>
    public void Disconnect()
    {
        lock (_stateLock)
        {
            StopSending();

            var port = _port;
            _port = null;
            if (port is null)
            {
                return;
            }

            try
            {
                if (port.IsOpen)
                {
                    byte[] bye = DmxFrame.HandshakeDisconnect();
                    port.Write(bye, 0, bye.Length);
                }
            }
            catch (Exception)
            {
                // The box may already have dropped the link: close anyway.
            }
            SafeClose(port);
        }
    }

    /// <summary>Next frame counter (0..255, wraps around).</summary>
    private int NextCodeNum()
    {
        _codeNum = (_codeNum + 1) & 0xFF;
        return _codeNum;
    }

    /// <summary>Send loop (background thread): computes the universe, builds the frame, writes it, waits for the next one.</summary>
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
                LastError = FriendlyError(ex);
                _sending = false;
                try
                {
                    ConnectionLost?.Invoke(this, LastError);
                }
                catch (Exception)
                {
                    // A faulty subscriber (window already closed...) must not crash the whole process
                    // from this thread.
                }
                return;
            }

            // Aim for a steady rate: subtract the time already spent computing.
            long elapsed = clock.ElapsedMilliseconds - started;
            Thread.Sleep((int)Math.Max(1, _sendIntervalMs - elapsed));
        }
    }

    /// <summary>Disconnects (handshake and port closed).</summary>
    public void Dispose() => Disconnect();
}
