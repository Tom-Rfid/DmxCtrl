// =====================================================================================
//  FogPanel.cs
// -------------------------------------------------------------------------------------
//  Machine a fumee (ex. Ayra WSM02 MKIII, 1 canal DMX = debit) :
//
//    - FogStrip  : une ligne d'etat + commandes pour UNE machine :
//        [PRETE / CHAUFFE]  [FUMEE 100 % / PAS DE FUMEE]  [auto : prochain jet dans..]
//        [Fumee (maintenir)] [Coup de X s] [curseur 0,5-5 s] [Manuel | Auto] [Stop]
//    - FogBanner : le bandeau en haut de la fenetre (visible sur tous les onglets),
//      une FogStrip par machine active du patch, et avec 2 machines ou plus une ligne
//      « Toutes les machines » (maintenir / coup de chacune) ;
//    - FogPanel  : l'onglet de la machine : la meme FogStrip + les reglages
//      (debit, duree d'un jet, delai entre deux jets en auto, temps de chauffe).
//
//  IMPORTANT : le DMX ne va que dans un sens. La machine ne renvoie ni son etat
//  « prete » ni sa temperature : l'etat « prete » est une ESTIMATION a partir de
//  l'heure d'allumage (bouton « Je viens d'allumer la machine ») et du temps de chauffe.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Engine;
using DmxController.Core.Model;

namespace DmxController.UI;

/// <summary>Etat + commandes d'une machine a fumee, sur une ligne.</summary>
[DesignerCategory("Code")]
internal sealed class FogStrip : FlowLayoutPanel
{
    private readonly ShowContext _ctx;
    private readonly FixtureInstance _fixture;
    private readonly Label _ready = Chip();
    private readonly Label _output = Chip();
    private readonly Label _info = new() { AutoSize = true, Margin = new Padding(6, 9, 3, 3), ForeColor = Color.DimGray };
    private readonly Button _hold = new() { Text = "FUMÉE (maintenir)", AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(8, 3, 3, 3) };
    private readonly Button _shot = new() { AutoSize = true, Margin = new Padding(3) };
    private readonly TrackBar _shotLength = Ui.Slider(1, (int)(FogSettings.MaxShotSeconds * 2), 110); // demi-secondes
    private readonly CheckBox _auto = new() { Text = "Auto", Appearance = Appearance.Button, AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = true, Margin = new Padding(3) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private bool _updating;

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
        Controls.AddRange(new Control[] { _ready, _output, _hold, _shot, _shotLength, _auto, _stop, _info });
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
        tips.SetToolTip(_hold, "Fume tant que le bouton est enfoncé.");
        tips.SetToolTip(_shot, "Un jet de la durée réglée avec le curseur à droite (0,5 à 5 s).");
        tips.SetToolTip(_shotLength, "Durée du bouton « Coup » : 0,5 à 5 s.");
        tips.SetToolTip(_auto, "Mode auto : un jet, une pause (délai), un jet… Réglages dans l'onglet de la machine.");
        tips.SetToolTip(_ready, "ESTIMATION : la machine ne renvoie rien en DMX. Clique « Je viens d'allumer la machine » dans son onglet pour lancer le décompte de chauffe.");
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
            _ctx.Status($"{_fixture.Name} : coup de fumée de {_fixture.Fog.ShotSeconds:0.#} s.");
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
                ? $"{_fixture.Name} : mode AUTO ({_fixture.Fog.DurationSeconds:0.#} s de fumée toutes les {_fixture.Fog.DurationSeconds + _fixture.Fog.DelaySeconds:0.#} s)."
                : $"{_fixture.Name} : mode manuel.");
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

    private static Label Chip() => new()
    {
        AutoSize = true,
        Font = Ui.BoldFont,
        Padding = new Padding(6, 3, 6, 3),
        Margin = new Padding(4, 5, 2, 3),
        ForeColor = Color.White,
    };

    private void ReleaseHold()
    {
        _ctx.Engine.SetFogHold(_fixture.Id, false);
        UpdateStatus();
    }

    public void UpdateStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        FogStatus s = _ctx.Engine.GetFogStatus(_fixture.Id);

        // Prete / chauffe (estimation)
        if (s.WarmupRemaining is null)
        {
            _ready.Text = "PRÊTE ?";
            _ready.BackColor = Color.Gray;
        }
        else if (s.WarmupRemaining > 0)
        {
            var left = TimeSpan.FromSeconds(Math.Ceiling(s.WarmupRemaining.Value));
            _ready.Text = $"CHAUFFE {left:m\\:ss}";
            _ready.BackColor = Color.FromArgb(200, 110, 0);
        }
        else
        {
            _ready.Text = "PRÊTE";
            _ready.BackColor = Ui.OkColor;
        }

        // Fumee ou pas
        _output.Text = !_fixture.Enabled ? "DÉSACTIVÉE" : s.Fogging ? $"FUMÉE {s.OutputPercent} %" : "PAS DE FUMÉE";
        _output.BackColor = !_fixture.Enabled ? Color.Gray : s.Fogging ? Color.FromArgb(30, 110, 200) : Color.FromArgb(70, 74, 86);

        _shot.Text = $"Coup de {_fixture.Fog.ShotSeconds:0.#} s";
        _updating = true;
        _auto.Checked = s.Auto;
        _updating = false;
        _auto.Text = s.Auto ? "AUTO (actif)" : "Manuel → Auto";
        _auto.BackColor = s.Auto ? Color.FromArgb(30, 110, 200) : SystemColors.Control;
        _auto.ForeColor = s.Auto ? Color.White : SystemColors.ControlText;
        _auto.UseVisualStyleBackColor = !s.Auto;

        _info.Text = s.Auto
            ? (s.Fogging ? $"jet en cours : {s.JetRemaining:0} s" : $"prochain jet dans {s.NextJetIn:0} s")
            : s.JetRemaining > 0 ? $"coup en cours : {s.JetRemaining:0.0} s" : "Température : non transmise en DMX";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Bandeau en haut de la fenetre : une ligne par machine a fumee active.</summary>
[DesignerCategory("Code")]
internal sealed class FogBanner : FlowLayoutPanel
{
    private readonly ShowContext _ctx;

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

    /// <summary>Recree les lignes d'apres le patch (apres une modification du patch).</summary>
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

    /// <summary>Commandes groupees : toutes les machines fument (maintenir), ou un coup chacune avec SA duree.</summary>
    private Control BuildAllMachinesRow(List<FixtureInstance> machines)
    {
        var hold = new Button { Text = "FUMÉE TOUTES (maintenir)", AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(8, 3, 3, 3) };
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

        var shot = new Button { Text = "Coup toutes (durée de chacune)", AutoSize = true, Margin = new Padding(3) };
        shot.Click += (_, _) =>
        {
            foreach (var m in machines)
            {
                _ctx.Engine.FogShot(m.Id);
            }
            _ctx.Status("Coup de fumée sur toutes les machines : " +
                        string.Join(", ", machines.Select(m => $"{m.Name} {m.Fog.ShotSeconds:0.#} s")) + ".");
        };
        return Ui.Flow(0, Ui.Text($"Toutes les machines ({machines.Count}) :", bold: true), hold, shot);
    }
}

/// <summary>Onglet d'une machine a fumee : etat, commandes et reglages.</summary>
[DesignerCategory("Code")]
internal sealed class FogPanel : UserControl
{
    private readonly ShowContext _ctx;
    private readonly FixtureInstance _fixture;
    private readonly NumericUpDown _level = new() { Minimum = 1, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _duration = new() { Minimum = 0.5m, Maximum = 600, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };
    private readonly NumericUpDown _delay = new() { Minimum = 1, Maximum = 3600, Width = 70 };
    private readonly NumericUpDown _warmup = new() { Minimum = 0, Maximum = 30, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };
    private readonly Label _cycle = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 3) };
    private bool _loading;

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
            ? profile.Name
            : $"{profile.Manufacturer} {profile.Name}".Trim();
        page.Controls.Add(new Label { Text = $"{fixture.Name}  —  {model}", AutoSize = true, Font = Ui.TitleFont });
        page.Controls.Add(Ui.Text($"Adresse DMX {fixture.Address} : canaux {fixture.Address} à {fixture.EndAddress(profile)}", color: Color.DimGray));

        // ---------------- Etat et commandes ----------------
        var strip = new FogStrip(ctx, fixture, showName: false);
        var powered = new Button { Text = "Je viens d'allumer la machine (décompte de chauffe)", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        powered.Click += (_, _) =>
        {
            _ctx.Engine.MarkFogPoweredOn(_fixture.Id);
            strip.UpdateStatus();
            _ctx.Status($"{_fixture.Name} : chauffe estimée à {_fixture.Fog.WarmupMinutes:0.#} min à partir de maintenant.");
        };
        var commands = Ui.Table(1);
        commands.Controls.Add(strip);
        commands.Controls.Add(powered);
        commands.Controls.Add(new Label
        {
            Text = "« PRÊTE » est une estimation : le DMX ne va que dans un sens, la machine ne renvoie ni son voyant « prête » " +
                   "(il est dans le réservoir) ni sa température. Pendant la chauffe, la machine ne fume pas même si on lui demande.",
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 8, 3, 3),
        });
        page.Controls.Add(Ui.Group("État et commandes", commands));

        // ---------------- Reglages ----------------
        var settings = Ui.Table(3);
        Ui.Row(settings, "Débit de fumée", _level, Ui.Text("%"));
        Ui.Row(settings, "Durée d'un jet (auto)", _duration, Ui.Text("s  (mode auto ; le bouton « Coup » a son propre curseur, 0,5 à 5 s)"));
        Ui.Row(settings, "Délai entre deux jets", _delay, Ui.Text("s  (mode auto : pause après chaque jet)"));
        Ui.Row(settings, "Temps de chauffe", _warmup, Ui.Text("min  (notice Ayra WSM02 : environ 6 min)"));
        settings.Controls.Add(_cycle);
        settings.SetColumnSpan(_cycle, 3);
        page.Controls.Add(Ui.Group("Réglages (enregistrés avec la configuration)", settings));

        if (!string.IsNullOrWhiteSpace(profile.Notes))
        {
            page.Controls.Add(new Label { Text = profile.Notes, AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DimGray, Margin = new Padding(6) });
        }

        Controls.Add(page);

        _loading = true;
        Ui.SetValue(_level, fixture.Fog.LevelPercent);
        Ui.SetValue(_duration, fixture.Fog.DurationSeconds);
        Ui.SetValue(_delay, fixture.Fog.DelaySeconds);
        Ui.SetValue(_warmup, fixture.Fog.WarmupMinutes);
        _loading = false;
        UpdateCycleText();

        foreach (var box in new[] { _level, _duration, _delay, _warmup })
        {
            box.ValueChanged += (_, _) => SaveSettings();
        }
    }

    private void SaveSettings()
    {
        if (_loading)
        {
            return;
        }
        int level = (int)_level.Value;
        double duration = (double)_duration.Value;
        double delay = (double)_delay.Value;
        double warmup = (double)_warmup.Value;
        _ctx.Engine.EditShow(_ =>
        {
            _fixture.Fog.LevelPercent = level;
            _fixture.Fog.DurationSeconds = duration;
            _fixture.Fog.DelaySeconds = delay;
            _fixture.Fog.WarmupMinutes = warmup;
        });
        _ctx.MarkDirty();
        UpdateCycleText();
    }

    private void UpdateCycleText()
    {
        var fog = _fixture.Fog;
        _cycle.Text = $"Mode auto : {fog.DurationSeconds:0.#} s de fumée à {fog.LevelPercent} %, puis {fog.DelaySeconds:0} s de pause " +
                      $"→ un jet toutes les {fog.DurationSeconds + fog.DelaySeconds:0.#} s.";
    }
}
