// =====================================================================================
//  DebugPanel.cs
// -------------------------------------------------------------------------------------
//  "Debug" tab: the original test page, extended.
//    - RAW sliders on 48 consecutive DMX channels (first channel selectable),
//      each labelled with the fixture and the function it is supposed to drive;
//    - "Priority debug mode": while it is on, these raw values replace the WHOLE
//      output (the Show console is ignored). It switches on by itself as soon as
//      a slider is touched here, and the status bar shows it in red;
//    - blink test (the one that validated the protocol);
//    - monitor of what is REALLY sent to the box;
//    - send rate (100 ms by default, like the original app);
//    - event log (connection, errors, presets...).
//
//  Typical use: check a profile channel by channel (move the slider, watch the
//  fixture, tick "OK" in the fixture's tab).
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.UI;

/// <summary>"Debug" tab: raw channel sliders, priority debug mode, blink test, output monitor and log.</summary>
[DesignerCategory("Code")]
internal sealed class DebugPanel : UserControl
{
    private const int VisibleChannels = 48;

    private readonly ShowContext _ctx;
    private readonly byte[] _buffer = DmxFrame.NewUniverse();
    private readonly CheckBox _active = new()
    {
        Text = Tr.T("Mode debug prioritaire (remplace TOUTE la sortie live)"),
        AutoSize = true,
        Margin = new Padding(3, 8, 12, 3),
        Font = Ui.BoldFont,
    };
    private readonly Button _blink = new() { Text = Tr.T("Démarrer le test clignotement"), AutoSize = true };
    private readonly NumericUpDown _start = new() { Minimum = 1, Maximum = DmxFrame.UniverseSize - VisibleChannels + 1, Width = 64, Margin = new Padding(3, 5, 3, 3) };
    private readonly CheckBox _onChange = new() { Text = Tr.T("   Seulement si changement, maintien toutes les"), AutoSize = true, Margin = new Padding(3, 7, 0, 3) };
    private readonly NumericUpDown _keepAlive = new() { Minimum = 100, Maximum = 5000, Increment = 100, Value = 1000, Width = 64, Margin = new Padding(3, 5, 3, 3) };
    private readonly NumericUpDown _interval = new() { Minimum = 20, Maximum = 1000, Increment = 10, Value = 100, Width = 64, Margin = new Padding(3, 5, 3, 3) };
    private readonly TrackBar[] _sliders = new TrackBar[VisibleChannels];
    private readonly Label[] _labels = new Label[VisibleChannels];
    private readonly Label[] _values = new Label[VisibleChannels];
    private readonly DmxMonitor _monitor = new() { Dock = DockStyle.Bottom, Height = 120 };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Bottom,
        Height = 110,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f),
        BackColor = Color.FromArgb(250, 250, 250),
    };
    private System.Windows.Forms.Timer? _blinkTimer;
    private bool _blinkOn;
    private bool _updating;

    /// <summary>Builds the tab: toolbar, raw sliders, monitor and event log.</summary>
    public DebugPanel(ShowContext ctx)
    {
        _ctx = ctx;
        Dock = DockStyle.Fill;

        // ---------------- Toolbar ----------------
        var zero = new Button { Text = Tr.T("Tout à 0"), AutoSize = true };
        zero.Click += (_, _) =>
        {
            StopBlink();
            Array.Clear(_buffer);
            PushBuffer();
            RefreshSliders();
        };
        var copy = new Button { Text = Tr.T("Copier la sortie live actuelle"), AutoSize = true };
        copy.Click += (_, _) =>
        {
            Array.Copy(_ctx.Engine.GetLastOutput(), _buffer, _buffer.Length);
            PushBuffer();
            RefreshSliders();
        };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 2) };
        toolbar.Controls.AddRange(new Control[]
        {
            _active, _blink, zero, copy,
            Ui.Text(Tr.T("Canaux affichés à partir de")), _start,
            Ui.Text(Tr.T("   Envoi d'une trame toutes les")), _interval, Ui.Text(Tr.T("ms (100 = appli d'origine)")),
            _onChange, _keepAlive, Ui.Text(Tr.T("ms")),
        });
        var tips = new ToolTip();
        tips.SetToolTip(_onChange, Tr.T("Coché : une trame seulement quand une valeur change, plus une trame de maintien toutes les N ms pour que le boîtier et les appareils ne perdent pas le signal. À tester sur ton matériel avant une soirée."));
        Disposed += (_, _) => tips.Dispose();

        var help = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 2, 10, 6),
            ForeColor = Color.DimGray,
            Text = Tr.T("Chaque curseur pilote un canal DMX brut. L'étiquette indique l'appareil et la fonction prévue par son profil ; ") +
                   Tr.T("« (?) » = pas encore vérifié. Pense à décocher le mode debug avant la soirée."),
        };

        // ---------------- Raw sliders ----------------
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(4) };
        for (int i = 0; i < VisibleChannels; i++)
        {
            int slot = i;
            var cell = new Panel { Size = new Size(400, 30), Margin = new Padding(2) };
            _labels[i] = new Label { Location = new Point(0, 6), Size = new Size(220, 20), AutoEllipsis = true };
            _sliders[i] = Ui.Slider(0, 255, 140);
            _sliders[i].Location = new Point(222, 0);
            _sliders[i].ValueChanged += (_, _) => OnSlider(slot);
            _values[i] = new Label { Location = new Point(366, 6), Size = new Size(34, 20) };
            cell.Controls.AddRange(new Control[] { _labels[i], _sliders[i], _values[i] });
            flow.Controls.Add(cell);
        }

        var monitorTitle = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(8, 6, 8, 2),
            Font = Ui.BoldFont,
            Text = Tr.T("Moniteur : valeurs réellement envoyées au boîtier (ou qui le seraient si on était connecté)"),
        };

        // Order of adding: Fill first; the last control added is docked first.
        Controls.Add(flow);
        Controls.Add(monitorTitle);
        Controls.Add(_monitor);
        Controls.Add(_log);
        Controls.Add(help);
        Controls.Add(toolbar);

        // ---------------- Events ----------------
        _active.CheckedChanged += (_, _) =>
        {
            _ctx.Engine.DebugActive = _active.Checked;
            if (!_active.Checked)
            {
                StopBlink();
            }
            _ctx.NotifyDebugModeChanged();
        };
        _blink.Click += (_, _) => ToggleBlink();
        _start.ValueChanged += (_, _) => RebuildLabels();
        _interval.ValueChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }
            _ctx.Link.SendIntervalMs = (int)_interval.Value;
            _ctx.Show.Settings.SendIntervalMs = (int)_interval.Value;
            _ctx.MarkDirty();
        };
        _onChange.CheckedChanged += (_, _) => SaveSendMode();
        _keepAlive.ValueChanged += (_, _) => SaveSendMode();

        ReloadSettings();
        RebuildLabels();
    }

    /// <summary>Reads the configuration settings again (after a file was opened).</summary>
    public void ReloadSettings()
    {
        _updating = true;
        Ui.SetValue(_interval, _ctx.Show.Settings.SendIntervalMs);
        _onChange.Checked = _ctx.Show.Settings.SendOnChangeOnly;
        Ui.SetValue(_keepAlive, _ctx.Show.Settings.KeepAliveMs);
        _keepAlive.Enabled = _onChange.Checked;
        _updating = false;
    }

    /// <summary>"Only when something changes" box and keep-alive time: applied to the link and saved with the configuration.</summary>
    private void SaveSendMode()
    {
        _keepAlive.Enabled = _onChange.Checked;
        if (_updating)
        {
            return;
        }
        _ctx.Link.SendOnChangeOnly = _onChange.Checked;
        _ctx.Link.KeepAliveMs = (int)_keepAlive.Value;
        _ctx.Show.Settings.SendOnChangeOnly = _onChange.Checked;
        _ctx.Show.Settings.KeepAliveMs = (int)_keepAlive.Value;
        _ctx.MarkDirty();
        _ctx.Status(_onChange.Checked
            ? Tr.F("Envoi seulement si changement (maintien toutes les {0} ms).", (int)_keepAlive.Value)
            : Tr.T("Envoi continu d'une trame à chaque intervalle."));
    }

    /// <summary>Relabels the sliders from the patch (after the patch or a profile changed).</summary>
    public void RebuildLabels()
    {
        int first = (int)_start.Value;
        for (int i = 0; i < VisibleChannels; i++)
        {
            int channel = first + i;
            _labels[i].Text = $"{channel} · {Describe(channel)}";
        }
        _monitor.ChannelCount = Math.Max(48, (int)Math.Ceiling(LastPatchedChannel() / 8.0) * 8);
        RefreshSliders();
    }

    /// <summary>Shows the last universe sent (called by the main window timer).</summary>
    public void UpdateMonitor(byte[] output) => _monitor.SetValues(output);

    /// <summary>Adds a time-stamped line to the event log (keeps the end of the log).</summary>
    public void AppendLog(string message)
    {
        if (_log.TextLength > 30000)
        {
            _log.Text = _log.Text[^15000..]; // keep the end of the log
        }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    /// <summary>Label of a DMX channel: fixture and channel function, or "(free)".</summary>
    private string Describe(int channel)
    {
        foreach (var fixture in _ctx.Show.Fixtures)
        {
            var profile = _ctx.ProfileOf(fixture);
            if (profile is null || channel < fixture.Address || channel > fixture.EndAddress(profile))
            {
                continue;
            }
            var def = profile.Channels[channel - fixture.Address];
            return $"{Tr.T(fixture.Name)} › {Tr.T(def.Name)}" + (def.Verified ? "" : " (?)") + (fixture.Enabled ? "" : Tr.T(" [désactivé]"));
        }
        return Tr.T("(libre)");
    }

    /// <summary>Highest DMX channel used by an enabled fixture (for the blink test).</summary>
    private int LastPatchedChannel()
    {
        int last = 0;
        foreach (var fixture in _ctx.Show.Fixtures)
        {
            var profile = _ctx.ProfileOf(fixture);
            if (profile is not null)
            {
                last = Math.Max(last, Math.Min(DmxFrame.UniverseSize, fixture.EndAddress(profile)));
            }
        }
        return last;
    }

    /// <summary>A raw slider moved: copies its value into the debug universe and turns debug mode on.</summary>
    private void OnSlider(int slot)
    {
        if (_updating)
        {
            return;
        }

        int channel = (int)_start.Value + slot;
        _buffer[channel] = (byte)_sliders[slot].Value;
        _values[slot].Text = _buffer[channel].ToString();
        PushBuffer();

        // Touching a slider here turns debug mode on, otherwise it would have no visible effect.
        if (!_active.Checked)
        {
            _active.Checked = true;
        }
    }

    /// <summary>Sends the raw debug universe to the engine.</summary>
    private void PushBuffer() => _ctx.Engine.SetDebugUniverse(_buffer);

    /// <summary>Labels and values of the sliders for the channels currently shown.</summary>
    private void RefreshSliders()
    {
        _updating = true;
        try
        {
            int first = (int)_start.Value;
            for (int i = 0; i < VisibleChannels; i++)
            {
                _sliders[i].Value = _buffer[first + i];
                _values[i].Text = _buffer[first + i].ToString();
            }
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>
    /// Blink test: every patched channel alternates between 255 and 0 every second.
    /// Checks the link without knowing anything about the profiles.
    /// </summary>
    private void ToggleBlink()
    {
        if (_blinkTimer is not null)
        {
            StopBlink();
            return;
        }

        int last = Math.Max(1, LastPatchedChannel());
        _active.Checked = true;
        _blinkOn = false;
        _blinkTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            for (int ch = 1; ch <= last; ch++)
            {
                _buffer[ch] = _blinkOn ? (byte)255 : (byte)0;
            }
            PushBuffer();
            RefreshSliders();
        };
        _blinkTimer.Start();
        _blink.Text = Tr.T("Arrêter le test clignotement");
        _ctx.Status(Tr.F("Test clignotement : canaux 1 à {0}, une fois par seconde.", last));
    }

    /// <summary>Stops the blink test and puts the channels back to 0.</summary>
    private void StopBlink()
    {
        if (_blinkTimer is null)
        {
            return;
        }
        _blinkTimer.Stop();
        _blinkTimer.Dispose();
        _blinkTimer = null;
        _blink.Text = Tr.T("Démarrer le test clignotement");
    }

    /// <summary>Stops the timers and unsubscribes before the tab is destroyed.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopBlink();
        }
        base.Dispose(disposing);
    }
}
