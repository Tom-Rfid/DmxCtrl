// =====================================================================================
//  DebugPanel.cs
// -------------------------------------------------------------------------------------
//  Onglet "Debug" : la page de test d'origine, enrichie.
//    - curseurs BRUTS sur 48 canaux DMX consecutifs (choix du premier canal),
//      chacun etiquete avec l'appareil et la fonction qu'il est cense piloter ;
//    - "Mode debug prioritaire" : quand il est actif, ces valeurs brutes remplacent
//      TOUTE la sortie (la console Soiree est ignoree). Il s'active tout seul des
//      qu'on touche un curseur ici, et la barre d'etat le signale en rouge ;
//    - test de clignotement (celui qui a valide le protocole) ;
//    - moniteur de ce qui part REELLEMENT vers le boitier ;
//    - cadence d'envoi (100 ms par defaut, comme l'appli d'origine) ;
//    - journal des evenements (connexion, erreurs, presets...).
//
//  Usage typique : verifier un profil canal par canal (bouger le curseur, regarder
//  la lyre, cocher "OK" dans l'onglet de l'appareil).
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;
using DmxController.Core.Protocol;

namespace DmxController.UI;

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

    public DebugPanel(ShowContext ctx)
    {
        _ctx = ctx;
        Dock = DockStyle.Fill;

        // ---------------- Barre d'outils ----------------
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
        });

        var help = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 2, 10, 6),
            ForeColor = Color.DimGray,
            Text = Tr.T("Chaque curseur pilote un canal DMX brut. L'étiquette indique l'appareil et la fonction prévue par son profil ; ") +
                   Tr.T("« (?) » = pas encore vérifié. Pense à décocher le mode debug avant la soirée."),
        };

        // ---------------- Curseurs bruts ----------------
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

        // Ordre d'ajout : Fill d'abord ; le dernier ajoute est docke en premier.
        Controls.Add(flow);
        Controls.Add(monitorTitle);
        Controls.Add(_monitor);
        Controls.Add(_log);
        Controls.Add(help);
        Controls.Add(toolbar);

        // ---------------- Evenements ----------------
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

        ReloadSettings();
        RebuildLabels();
    }

    /// <summary>Relit les reglages de la configuration (apres ouverture d'un fichier).</summary>
    public void ReloadSettings()
    {
        _updating = true;
        Ui.SetValue(_interval, _ctx.Show.Settings.SendIntervalMs);
        _updating = false;
    }

    /// <summary>Re-etiquette les curseurs d'apres le patch (apres modification du patch ou d'un profil).</summary>
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

    public void UpdateMonitor(byte[] output) => _monitor.SetValues(output);

    public void AppendLog(string message)
    {
        if (_log.TextLength > 30000)
        {
            _log.Text = _log.Text[^15000..]; // on garde la fin du journal
        }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

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
            return $"{fixture.Name} › {Tr.T(def.Name)}" + (def.Verified ? "" : " (?)") + (fixture.Enabled ? "" : Tr.T(" [désactivé]"));
        }
        return Tr.T("(libre)");
    }

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

        // Toucher un curseur ici active le mode debug, sinon il n'aurait aucun effet visible.
        if (!_active.Checked)
        {
            _active.Checked = true;
        }
    }

    private void PushBuffer() => _ctx.Engine.SetDebugUniverse(_buffer);

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
    /// Test de clignotement : tous les canaux patches alternent entre 255 et 0 chaque
    /// seconde. Sert a verifier la liaison sans rien connaitre des profils.
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopBlink();
        }
        base.Dispose(disposing);
    }
}
