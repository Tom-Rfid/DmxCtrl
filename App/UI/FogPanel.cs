// =====================================================================================
//  FogPanel.cs
// -------------------------------------------------------------------------------------
//  Fog machine (e.g. Ayra WSM02 MKIII, 1 DMX channel = output):
//
//    - FogStrip  : one row of status + controls for ONE machine:
//        [READY / WARMING]  [FOG 100 % / NO FOG]  [auto: next burst in..]
//        [Fog (hold)] [Burst X s] [0.5-5 s slider] [Manual | Auto] [every N min] [Stop]
//    - FogBanner : the banner at the top of the window (visible on every tab),
//      one FogStrip per enabled machine of the patch, and with 2 machines or more
//      an "All machines" row (hold / burst of each one);
//    - FogPanel  : the machine tab: the same FogStrip + the settings
//      (output, burst length, warm-up time).
//
//  IMPORTANT: DMX only goes one way. The machine sends back neither its "ready"
//  state nor its temperature: the "ready" state is an ESTIMATE from the time it was
//  switched on ("I just switched the machine on" button) and the warm-up time.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Engine;
using DmxController.Core.Model;

namespace DmxController.UI;

/// <summary>Status + controls of one fog machine, on one row.</summary>
[DesignerCategory("Code")]
internal sealed class FogStrip : FlowLayoutPanel
{
    private readonly ShowContext _ctx;
    private readonly FixtureInstance _fixture;
    private readonly Label _ready = Chip();
    private readonly Label _output = Chip();
    private readonly Label _info = new() { AutoSize = true, Margin = new Padding(6, 9, 3, 3), ForeColor = Color.DimGray };
    private readonly Button _hold = new() { Text = Tr.T("FUMÉE (maintenir)"), AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(8, 3, 3, 3) };
    private readonly Button _shot = new() { Margin = new Padding(3) }; // fixed width: does not move with the length
    private readonly TrackBar _shotLength = Ui.Slider(1, (int)(FogSettings.MaxShotSeconds * 2), 110); // half seconds
    private readonly NumericUpDown _interval = new() { Minimum = 1, Maximum = 30, Width = 48, Margin = new Padding(0, 6, 0, 3) };
    private readonly CheckBox _auto = new() { Text = Tr.T("Auto"), Appearance = Appearance.Button, AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _stop = new() { Text = Tr.T("Stop"), AutoSize = true, Margin = new Padding(3) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private bool _updating;

    /// <summary>Builds the row for <paramref name="fixture"/>; <paramref name="showName"/> adds its name first (banner).</summary>
    public FogStrip(ShowContext ctx, FixtureInstance fixture, bool showName)
    {
        _ctx = ctx;
        _fixture = fixture;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        Margin = new Padding(0);

        if (showName)
        {
            Controls.Add(Ui.Text(fixture.Name + " :", bold: true));
        }
        var every = new Label { Text = Tr.T("un jet toutes les"), AutoSize = true, Margin = new Padding(3, 9, 2, 3) };
        var minutes = new Label { Text = Tr.T("min"), AutoSize = true, Margin = new Padding(2, 9, 3, 3) };
        Controls.AddRange(new Control[] { _ready, _output, _hold, _shot, _shotLength, _auto, every, _interval, minutes, _stop, _info });
        // Width of the longest text ("Burst 2.5 s"): the button keeps the same size whatever the length.
        _shot.Size = new Size(TextRenderer.MeasureText(Tr.T("Coup de 2,5 s"), _shot.Font).Width + 20, 26);
        _interval.Value = fixture.Fog.IntervalMinutes;
        _interval.ValueChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }
            int n = (int)_interval.Value;
            _ctx.Engine.EditShow(_ => _fixture.Fog.SetIntervalMinutes(n));
            _ctx.MarkDirty();
            _ctx.Status(Tr.F("{0} : mode auto, un jet de {1:0.#} s toutes les {2} min.", _fixture.Name, _fixture.Fog.DurationSeconds, n));
            UpdateStatus();
        };
        _shotLength.Margin = new Padding(0, 3, 6, 0);
        _shotLength.Value = Math.Clamp((int)Math.Round(fixture.Fog.ShotSeconds * 2), _shotLength.Minimum, _shotLength.Maximum);
        _shotLength.ValueChanged += (_, _) =>
        {
            double seconds = _shotLength.Value / 2.0;
            _ctx.Engine.EditShow(_ => _fixture.Fog.ShotSeconds = seconds);
            _ctx.MarkDirty();
            UpdateStatus();
        };

        var tips = new ToolTip();
        tips.SetToolTip(_hold, Tr.T("Fume tant que le bouton est enfoncé."));
        tips.SetToolTip(_shot, Tr.T("Un jet de la durée réglée avec le curseur à droite (0,5 à 5 s)."));
        tips.SetToolTip(_shotLength, Tr.T("Durée du bouton « Coup » : 0,5 à 5 s."));
        tips.SetToolTip(_interval, Tr.T("Fréquence du mode auto : un jet toutes les 1 à 30 minutes."));
        tips.SetToolTip(_auto, Tr.T("Mode auto : un jet, une pause (délai), un jet… Réglages dans l'onglet de la machine."));
        tips.SetToolTip(_ready, Tr.T("ESTIMATION : la machine ne renvoie rien en DMX. Clique « Je viens d'allumer la machine » dans son onglet pour lancer le décompte de chauffe."));
        Disposed += (_, _) => tips.Dispose();

        _hold.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _ctx.Engine.SetFogHold(_fixture.Id, true);
                UpdateStatus();
            }
        };
        _hold.MouseUp += (_, _) => ReleaseHold();
        _hold.MouseCaptureChanged += (_, _) => ReleaseHold();
        _shot.Click += (_, _) =>
        {
            _ctx.Engine.FogShot(_fixture.Id);
            _ctx.Status(Tr.F("{0} : coup de fumée de {1:0.#} s.", _fixture.Name, _fixture.Fog.ShotSeconds));
            UpdateStatus();
        };
        _auto.CheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }
            _ctx.Engine.SetFogAuto(_fixture.Id, _auto.Checked);
            _ctx.Status(_auto.Checked
                ? Tr.F("{0} : mode AUTO ({1:0.#} s de fumée toutes les {2:0.#} s).", _fixture.Name, _fixture.Fog.DurationSeconds, (_fixture.Fog.DurationSeconds + _fixture.Fog.DelaySeconds))
                : Tr.F("{0} : mode manuel.", _fixture.Name));
            UpdateStatus();
        };
        _stop.Click += (_, _) =>
        {
            _ctx.Engine.StopFog(_fixture.Id);
            UpdateStatus();
        };

        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
        UpdateStatus();
    }

    /// <summary>Coloured status label ("READY", "FOG 100 %"...).</summary>
    private static Label Chip() => new()
    {
        AutoSize = true,
        Font = Ui.BoldFont,
        Padding = new Padding(6, 3, 6, 3),
        Margin = new Padding(4, 5, 2, 3),
        ForeColor = Color.White,
    };

    /// <summary>"Fog (hold)" button released: stops fogging.</summary>
    private void ReleaseHold()
    {
        _ctx.Engine.SetFogHold(_fixture.Id, false);
        UpdateStatus();
    }

    /// <summary>Refreshes the status chips, the buttons and the info text from the engine (4 times/s).</summary>
    public void UpdateStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        FogStatus s = _ctx.Engine.GetFogStatus(_fixture.Id);

        // Ready / warming (estimate)
        if (s.WarmupRemaining is null)
        {
            _ready.Text = Tr.T("PRÊTE ?");
            _ready.BackColor = Color.Gray;
        }
        else if (s.WarmupRemaining > 0)
        {
            var left = TimeSpan.FromSeconds(Math.Ceiling(s.WarmupRemaining.Value));
            _ready.Text = Tr.F("CHAUFFE {0:m\\:ss}", left);
            _ready.BackColor = Color.FromArgb(200, 110, 0);
        }
        else
        {
            _ready.Text = Tr.T("PRÊTE");
            _ready.BackColor = Ui.OkColor;
        }

        // Fog or not
        _output.Text = !_fixture.Enabled ? Tr.T("DÉSACTIVÉE") : s.Fogging ? Tr.F("FUMÉE {0} %", s.OutputPercent) : Tr.T("PAS DE FUMÉE");
        _output.BackColor = !_fixture.Enabled ? Color.Gray : s.Fogging ? Color.FromArgb(30, 110, 200) : Color.FromArgb(70, 74, 86);

        _shot.Text = Tr.F("Coup de {0:0.#} s", _fixture.Fog.ShotSeconds);
        _updating = true;
        _auto.Checked = s.Auto;
        if (!_interval.Focused && (int)_interval.Value != _fixture.Fog.IntervalMinutes)
        {
            _interval.Value = _fixture.Fog.IntervalMinutes; // changed elsewhere (banner / tab)
        }
        _updating = false;
        _auto.Text = s.Auto ? Tr.T("AUTO (actif)") : Tr.T("Manuel → Auto");
        _auto.BackColor = s.Auto ? Color.FromArgb(30, 110, 200) : SystemColors.Control;
        _auto.ForeColor = s.Auto ? Color.White : SystemColors.ControlText;
        _auto.UseVisualStyleBackColor = !s.Auto;

        _info.Text = s.Auto
            ? (s.Fogging ? Tr.F("jet en cours : {0:0} s", s.JetRemaining) : Tr.F("prochain jet dans {0:0} s", s.NextJetIn))
            : s.JetRemaining > 0 ? Tr.F("coup en cours : {0:0.0} s", s.JetRemaining) : Tr.T("Température : non transmise en DMX");
    }

    /// <summary>Stops the refresh timer.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Banner at the top of the window: one row per enabled fog machine.</summary>
[DesignerCategory("Code")]
internal sealed class FogBanner : FlowLayoutPanel
{
    private readonly ShowContext _ctx;

    /// <summary>Creates the empty banner (filled by Rebuild).</summary>
    public FogBanner(ShowContext ctx)
    {
        _ctx = ctx;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Padding = new Padding(6, 2, 6, 2);
        BackColor = Color.FromArgb(226, 234, 244);
    }

    /// <summary>Rebuilds the rows from the patch (after the patch changed).</summary>
    public void Rebuild()
    {
        SuspendLayout();
        foreach (Control old in Controls.Cast<Control>().ToList())
        {
            Controls.Remove(old);
            old.Dispose();
        }

        var machines = _ctx.Show.Fixtures.Where(f => f.Enabled && _ctx.ProfileOf(f)?.IsFogMachine == true).ToList();
        if (machines.Count >= 2)
        {
            Controls.Add(BuildAllMachinesRow(machines));
        }
        foreach (var fixture in machines)
        {
            Controls.Add(new FogStrip(_ctx, fixture, showName: true));
        }
        Visible = Controls.Count > 0;
        ResumeLayout();
    }

    /// <summary>Group controls: all machines fog (hold), or one burst each with ITS OWN length.</summary>
    private Control BuildAllMachinesRow(List<FixtureInstance> machines)
    {
        var hold = new Button { Text = Tr.T("FUMÉE TOUTES (maintenir)"), AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(8, 3, 3, 3) };
        void SetHold(bool on)
        {
            foreach (var m in machines)
            {
                _ctx.Engine.SetFogHold(m.Id, on);
            }
        }
        hold.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                SetHold(true);
            }
        };
        hold.MouseUp += (_, _) => SetHold(false);
        hold.MouseCaptureChanged += (_, _) => SetHold(false);

        var shot = new Button { Text = Tr.T("Coup toutes (durée de chacune)"), AutoSize = true, Margin = new Padding(3) };
        shot.Click += (_, _) =>
        {
            foreach (var m in machines)
            {
                _ctx.Engine.FogShot(m.Id);
            }
            _ctx.Status(Tr.T("Coup de fumée sur toutes les machines : ") +
                        string.Join(", ", machines.Select(m => Tr.F("{0} {1:0.#} s", m.Name, m.Fog.ShotSeconds))) + ".");
        };
        return Ui.Flow(0, Ui.Text(Tr.F("Toutes les machines ({0}) :", machines.Count), bold: true), hold, shot);
    }
}

/// <summary>Tab of a fog machine: status, controls and settings.</summary>
[DesignerCategory("Code")]
internal sealed class FogPanel : UserControl
{
    private readonly ShowContext _ctx;
    private readonly FixtureInstance _fixture;
    private readonly NumericUpDown _level = new() { Minimum = 1, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _duration = new() { Minimum = 0.5m, Maximum = 600, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };
    private readonly NumericUpDown _warmup = new() { Minimum = 0, Maximum = 30, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };
    private readonly Label _cycle = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 3) };
    private bool _loading;

    /// <summary>Builds the tab of <paramref name="fixture"/>: status row, warm-up button and settings.</summary>
    public FogPanel(ShowContext ctx, FixtureInstance fixture, FixtureProfile profile)
    {
        _ctx = ctx;
        _fixture = fixture;
        Dock = DockStyle.Fill;
        AutoScroll = true;

        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 8, 10, 8),
        };

        string model = profile.Name.StartsWith(profile.Manufacturer, StringComparison.OrdinalIgnoreCase)
            ? Tr.T(profile.Name)
            : $"{profile.Manufacturer} {Tr.T(profile.Name)}".Trim();
        page.Controls.Add(new Label { Text = $"{fixture.Name}  —  {model}", AutoSize = true, Font = Ui.TitleFont });
        page.Controls.Add(Ui.Text(Tr.F("Adresse DMX {0} : canaux {1} à {2}", fixture.Address, fixture.Address, fixture.EndAddress(profile)), color: Color.DimGray));

        // ---------------- Status and controls ----------------
        var strip = new FogStrip(ctx, fixture, showName: false);
        var powered = new Button { Text = Tr.T("Je viens d'allumer la machine (décompte de chauffe)"), AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        powered.Click += (_, _) =>
        {
            _ctx.Engine.MarkFogPoweredOn(_fixture.Id);
            strip.UpdateStatus();
            _ctx.Status(Tr.F("{0} : chauffe estimée à {1:0.#} min à partir de maintenant.", _fixture.Name, _fixture.Fog.WarmupMinutes));
        };
        var commands = Ui.Table(1);
        commands.Controls.Add(strip);
        commands.Controls.Add(powered);
        commands.Controls.Add(new Label
        {
            Text = Tr.T("« PRÊTE » est une estimation : le DMX ne va que dans un sens, la machine ne renvoie ni son voyant « prête » ") +
                   Tr.T("(il est dans le réservoir) ni sa température. Pendant la chauffe, la machine ne fume pas même si on lui demande."),
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 8, 3, 3),
        });
        page.Controls.Add(Ui.Group(Tr.T("État et commandes"), commands));

        // ---------------- Settings ----------------
        var settings = Ui.Table(3);
        Ui.Row(settings, Tr.T("Débit de fumée"), _level, Ui.Text(Tr.T("%")));
        Ui.Row(settings, Tr.T("Durée d'un jet (auto)"), _duration, Ui.Text(Tr.T("s  (mode auto ; le bouton « Coup » a son propre curseur, 0,5 à 5 s)")));
        Ui.Row(settings, Tr.T("Temps de chauffe"), _warmup, Ui.Text(Tr.T("min  (notice Ayra WSM02 : environ 6 min)")));
        settings.Controls.Add(_cycle);
        settings.SetColumnSpan(_cycle, 3);
        page.Controls.Add(Ui.Group(Tr.T("Réglages (enregistrés avec la configuration)"), settings));

        if (!string.IsNullOrWhiteSpace(profile.Notes))
        {
            page.Controls.Add(new Label { Text = Tr.T(profile.Notes), AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DimGray, Margin = new Padding(6) });
        }

        Controls.Add(page);

        _loading = true;
        Ui.SetValue(_level, fixture.Fog.LevelPercent);
        Ui.SetValue(_duration, fixture.Fog.DurationSeconds);
        Ui.SetValue(_warmup, fixture.Fog.WarmupMinutes);
        _loading = false;
        UpdateCycleText();

        foreach (var box in new[] { _level, _duration, _warmup })
        {
            box.ValueChanged += (_, _) => SaveSettings();
        }
    }

    /// <summary>Copies the settings boxes into the fixture (saved with the configuration).</summary>
    private void SaveSettings()
    {
        if (_loading)
        {
            return;
        }
        int level = (int)_level.Value;
        double duration = (double)_duration.Value;
        double warmup = (double)_warmup.Value;
        _ctx.Engine.EditShow(_ =>
        {
            _fixture.Fog.LevelPercent = level;
            int interval = _fixture.Fog.IntervalMinutes; // the frequency ("every N min" box) stays the same
            _fixture.Fog.DurationSeconds = duration;
            _fixture.Fog.SetIntervalMinutes(interval);
            _fixture.Fog.WarmupMinutes = warmup;
        });
        _ctx.MarkDirty();
        UpdateCycleText();
    }

    /// <summary>Sentence describing the auto cycle.</summary>
    private void UpdateCycleText()
    {
        var fog = _fixture.Fog;
        _cycle.Text = Tr.F("Mode auto : un jet de {0:0.#} s à {1} % toutes les {2} min ", fog.DurationSeconds, fog.LevelPercent, fog.IntervalMinutes) +
                      Tr.T("(fréquence réglable à côté du bouton Auto, 1 à 30 min).");
    }
}
