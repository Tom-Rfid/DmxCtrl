// =====================================================================================
//  LivePanel.cs
// -------------------------------------------------------------------------------------
//  Onglet "Soiree" : la console de jeu pendant la fete.
//
//   +-------------+------------------------------------------+----------------+
//   | Appareils   |  Couleurs        Mouvements              |  Master        |
//   | pilotes     |  Chenillard      Strobe                  |  BLACKOUT      |
//   | (coches)    |                                          |  FLASH         |
//   |             |                                          |  Tempo / TAP   |
//   +-------------+------------------------------------------+----------------+
//   | Scenes : boutons de rappel, enregistrement, mode auto                     |
//   +---------------------------------------------------------------------------+
//
//  Couleurs, mouvements, chenillard et strobe s'appliquent aux appareils COCHES
//  a gauche. Master, blackout et flash s'appliquent a tout.
//
//  Raccourcis clavier (geres par MainForm quand cet onglet est actif) :
//    Espace = blackout, F maintenu = flash, T = tap tempo, 1..9 = scenes.
// =====================================================================================

using System.ComponentModel;
using System.Diagnostics;
using DmxController.Core.Engine;
using DmxController.Core.Model;

namespace DmxController.UI;

[DesignerCategory("Code")]
internal sealed class LivePanel : UserControl
{
    private sealed record FixtureItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record ShapeItem(MovementShape Shape)
    {
        public override string ToString() => MovementShapeInfo.DisplayName(Shape);
    }

    private readonly ShowContext _ctx;
    private bool _updating;
    private readonly HashSet<Keys> _heldKeys = new();

    // --- Appareils ---
    private readonly CheckedListBox _fixtureList = new() { CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill };

    // --- Couleurs ---
    private readonly CheckBox _openDimmer = new() { Text = "Allumer le dimmer (s'il est à 0) en choisissant une couleur", AutoSize = true };

    // --- Mouvements ---
    private readonly ComboBox _shape = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(3, 4, 3, 3) };
    private readonly TrackBar _speed = Ui.Slider(1, 120, 200);
    private readonly Label _speedValue = Ui.ValueLabel(90);
    private readonly TrackBar _size = Ui.Slider(0, 100, 200);
    private readonly Label _sizeValue = Ui.ValueLabel(90);
    private readonly TrackBar _spread = Ui.Slider(0, 100, 200);
    private readonly Label _spreadValue = Ui.ValueLabel(90);
    private readonly CheckBox _mirror = new() { Text = "Miroir (une lyre sur deux inversée)", AutoSize = true };
    private readonly CheckBox _moveSync = new() { Text = "Caler sur le BPM, 1 cycle =", AutoSize = true, Margin = new Padding(3, 6, 0, 3) };
    private readonly NumericUpDown _beatsPerCycle = new() { Minimum = 1, Maximum = 64, Value = 8, Width = 52, Margin = new Padding(3, 4, 3, 3) };

    // --- Chenillard ---
    private readonly CheckBox _chaseOn = new() { Text = "Actif", AutoSize = true, Font = Ui.BoldFont };
    private readonly CheckBox _chaseAlternate = new() { Text = "Une couleur différente par appareil", AutoSize = true };
    private readonly NumericUpDown _chaseSeconds = new() { Minimum = 0.1m, Maximum = 60, DecimalPlaces = 1, Increment = 0.1m, Value = 1, Width = 56, Margin = new Padding(3, 4, 3, 3) };
    private readonly CheckBox _chaseSync = new() { Text = "ou caler sur le BPM :", AutoSize = true, Margin = new Padding(12, 6, 0, 3) };
    private readonly NumericUpDown _chaseBeats = new() { Minimum = 0.25m, Maximum = 64, DecimalPlaces = 2, Increment = 0.25m, Value = 2, Width = 60, Margin = new Padding(3, 4, 3, 3) };
    private readonly List<CheckBox> _paletteChecks = new();

    // --- Strobe ---
    private readonly CheckBox _strobeOn = new() { Text = "Actif (appareils cochés)", AutoSize = true, Font = Ui.BoldFont };
    private readonly TrackBar _strobeHz = Ui.Slider(1, 10, 200);
    private readonly Label _strobeValue = Ui.ValueLabel(90);

    // --- Master / blackout / flash / tempo ---
    private readonly TrackBar _master = Ui.Slider(0, 255, 160);
    private readonly Label _masterValue = Ui.ValueLabel(46);
    private readonly Button _blackout = new() { Size = new Size(210, 56), Font = Ui.BigFont, Margin = new Padding(6) };
    private readonly Button _flash = new() { Text = "FLASH  (F maintenu)", Size = new Size(210, 56), Font = Ui.BigFont, Margin = new Padding(6) };
    private readonly NumericUpDown _bpm = new() { Minimum = 30, Maximum = 300, DecimalPlaces = 1, Value = 120, Width = 70, Margin = new Padding(3, 4, 3, 3) };
    private readonly Button _tap = new() { Text = "TAP  (T)", Size = new Size(190, 40), Font = Ui.BigFont };
    private readonly NumericUpDown _fade = new() { Minimum = 0, Maximum = 20, DecimalPlaces = 1, Increment = 0.5m, Value = 1, Width = 60, Margin = new Padding(3, 4, 3, 3) };
    private readonly TapTempo _tapTempo = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // --- Scenes / mode auto ---
    private readonly FlowLayoutPanel _sceneFlow = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(4) };
    private readonly CheckBox _autoOn = new() { Text = "Mode auto : enchaîner les scènes toutes les", AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(24, 8, 0, 3) };
    private readonly NumericUpDown _autoSeconds = new() { Minimum = 1, Maximum = 600, Value = 8, Width = 56, Margin = new Padding(3, 5, 3, 3) };
    private readonly CheckBox _autoSync = new() { Text = "ou sur le BPM, tous les", AutoSize = true, Margin = new Padding(8, 8, 0, 3) };
    private readonly NumericUpDown _autoBeats = new() { Minimum = 1, Maximum = 256, Value = 16, Width = 56, Margin = new Padding(3, 5, 3, 3) };
    private readonly Label _autoStatus = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 8, 3, 3) };
    private readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 100 };
    private int _autoIndex = -1;
    private double _autoLastSwitch;
    private string? _activeSceneId;

    public LivePanel(ShowContext ctx)
    {
        _ctx = ctx;
        Dock = DockStyle.Fill;

        var center = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(4) };
        center.Controls.Add(BuildColorGroup());
        center.Controls.Add(BuildMovementGroup());
        center.Controls.Add(BuildChaseGroup());
        center.Controls.Add(BuildStrobeGroup());

        // Dernier ajoute = docke en premier : les scenes prennent toute la largeur en bas.
        Controls.Add(center);
        Controls.Add(BuildRightPanel());
        Controls.Add(BuildFixturePanel());
        Controls.Add(BuildScenePanel());

        WireEvents();

        _ctx.LiveStateChanged += RefreshFromEngine;
        _autoTimer.Tick += (_, _) => OnAutoTick();
        _autoTimer.Start();

        ReloadAll();
    }

    // =================================================================================
    //  Construction de l'interface
    // =================================================================================

    private Control BuildFixturePanel()
    {
        var group = new GroupBox { Text = "Appareils pilotés", Dock = DockStyle.Left, Width = 220, Padding = new Padding(8), Font = Ui.BoldFont };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Font = new Font("Segoe UI", 9f) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var all = new Button { Text = "Tous", Width = 58 };
        all.Click += (_, _) => CheckFixtures(_ => true);
        var none = new Button { Text = "Aucun", Width = 58 };
        none.Click += (_, _) => CheckFixtures(_ => false);
        var movers = new Button { Text = "Lyres", Width = 58 };
        movers.Click += (_, _) => CheckFixtures(f => _ctx.ProfileOf(f)?.CanMove == true);
        var others = new Button { Text = "Autres", Width = 58 };
        others.Click += (_, _) => CheckFixtures(f => _ctx.ProfileOf(f)?.CanMove == false);
        var invert = new Button { Text = "Inverser", Width = 58 };
        invert.Click += (_, _) =>
        {
            var current = _ctx.Engine.GetSelection().ToHashSet();
            CheckFixtures(f => !current.Contains(f.Id));
        };

        layout.Controls.Add(_fixtureList, 0, 0);
        layout.Controls.Add(Ui.Flow(200, all, none, movers, others, invert), 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "Couleurs, mouvements, chenillard et strobe s'appliquent aux appareils cochés.",
            AutoSize = true,
            MaximumSize = new Size(195, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 6, 3, 3),
        }, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildColorGroup()
    {
        var swatches = Ui.Flow(430);
        foreach (var color in ColorPalette.All)
        {
            var button = Ui.ColorButton(color);
            button.Click += (_, _) => ApplyColor(color);
            swatches.Controls.Add(button);
        }

        var on = new Button { Text = "Allumer 100 %", AutoSize = true };
        on.Click += (_, _) => ApplyToSelection(() => _ctx.Engine.SetDimmerForSelection(255), "Dimmer à 100 %");
        var off = new Button { Text = "Éteindre", AutoSize = true };
        off.Click += (_, _) => ApplyToSelection(() => _ctx.Engine.SetDimmerForSelection(0), "Dimmer à 0");
        var centerButton = new Button { Text = "Centrer les lyres", AutoSize = true };
        centerButton.Click += (_, _) => ApplyToSelection(_ctx.Engine.CenterSelection, "Lyres centrées");

        var table = Ui.Table(1);
        table.Controls.Add(swatches);
        table.Controls.Add(Ui.Flow(0, on, off, centerButton));
        table.Controls.Add(_openDimmer);
        return Ui.Group("Couleurs", table);
    }

    private Control BuildMovementGroup()
    {
        foreach (var shape in MovementShapeInfo.All)
        {
            _shape.Items.Add(new ShapeItem(shape));
        }

        var table = Ui.Table(3);
        Ui.Row(table, "Forme", _shape);
        Ui.Row(table, "Vitesse", _speed, _speedValue);
        Ui.Row(table, "Taille", _size, _sizeValue);
        Ui.Row(table, "Décalage", _spread, _spreadValue);
        Ui.Row(table, "", Ui.Flow(0, _moveSync, _beatsPerCycle, Ui.Text("temps")));
        Ui.Row(table, "", _mirror);
        table.Controls.Add(new Label
        {
            Text = "Décalage 0 % = lyres synchronisées, 100 % = réparties (2 lyres = opposées).\n" +
                   "Le mouvement tourne autour de la position réglée dans l'onglet de chaque lyre.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 4, 3, 3),
        });
        table.SetColumnSpan(table.Controls[^1], 3);
        return Ui.Group("Mouvements (lyres)", table);
    }

    private Control BuildChaseGroup()
    {
        var palette = Ui.Flow(400);
        foreach (var color in ColorPalette.All)
        {
            var check = new CheckBox
            {
                Text = color.Name,
                AutoSize = true,
                Tag = color.Name,
                BackColor = Color.FromArgb(60, color.R, color.G, color.B),
                Margin = new Padding(2),
            };
            check.CheckedChanged += (_, _) => PushEffects();
            _paletteChecks.Add(check);
            palette.Controls.Add(check);
        }

        var table = Ui.Table(2);
        Ui.Row(table, "", Ui.Flow(0, _chaseOn, _chaseAlternate));
        Ui.Row(table, "Pas", Ui.Flow(0, _chaseSeconds, Ui.Text("s"), _chaseSync, _chaseBeats, Ui.Text("temps")));
        Ui.Row(table, "Couleurs", palette);
        return Ui.Group("Chenillard de couleurs", table);
    }

    private Control BuildStrobeGroup()
    {
        var table = Ui.Table(3);
        Ui.Row(table, "", _strobeOn);
        Ui.Row(table, "Fréquence", _strobeHz, _strobeValue);
        table.Controls.Add(new Label
        {
            Text = "Strobe « logiciel » : coupe le dimmer en rythme.\nLimité par la cadence d'envoi (10 trames/s → 5 flashs/s max).",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 4, 3, 3),
        });
        table.SetColumnSpan(table.Controls[^1], 3);
        return Ui.Group("Strobe", table);
    }

    private Control BuildRightPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 236,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(2),
        };

        var master = Ui.Table(2);
        master.Controls.Add(_master);
        master.Controls.Add(_masterValue);
        panel.Controls.Add(Ui.Group("Master (intensité générale)", master));

        panel.Controls.Add(_blackout);
        panel.Controls.Add(_flash);

        var tempo = Ui.Table(2);
        Ui.Row(tempo, "BPM", _bpm);
        tempo.Controls.Add(_tap);
        tempo.SetColumnSpan(_tap, 2);
        Ui.Row(tempo, "Fondu scènes (s)", _fade);
        panel.Controls.Add(Ui.Group("Tempo", tempo));

        panel.Controls.Add(new Label
        {
            Text = "Raccourcis (onglet Soirée) :\nEspace = blackout\nF maintenu = flash\nT = tap tempo\n1 à 9 = scènes",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(8, 8, 3, 3),
        });
        return panel;
    }

    private Control BuildScenePanel()
    {
        var group = new GroupBox { Text = "Scènes", Dock = DockStyle.Bottom, Height = 196, Padding = new Padding(6), Font = Ui.BoldFont };

        var save = new Button { Text = "Enregistrer la scène actuelle…", AutoSize = true, Font = new Font("Segoe UI", 9f) };
        save.Click += (_, _) => SaveCurrentScene();

        var top = Ui.Flow(0, save, _autoOn, _autoSeconds, Ui.Text("s"), _autoSync, _autoBeats, Ui.Text("temps"), _autoStatus);
        top.Dock = DockStyle.Top;
        top.Font = new Font("Segoe UI", 9f);
        _sceneFlow.Font = new Font("Segoe UI", 9f);

        group.Controls.Add(_sceneFlow);
        group.Controls.Add(top);
        return group;
    }

    private void WireEvents()
    {
        // Selection d'appareils : ItemCheck est leve AVANT le changement, on calcule le nouvel etat.
        _fixtureList.ItemCheck += (_, e) =>
        {
            if (_updating)
            {
                return;
            }
            var ids = new List<string>();
            for (int i = 0; i < _fixtureList.Items.Count; i++)
            {
                bool on = i == e.Index ? e.NewValue == CheckState.Checked : _fixtureList.GetItemChecked(i);
                if (on)
                {
                    ids.Add(((FixtureItem)_fixtureList.Items[i]).Id);
                }
            }
            _ctx.Engine.SetSelection(ids);
        };

        // Tous les reglages d'effets passent par PushEffects().
        _shape.SelectedIndexChanged += (_, _) => PushEffects();
        foreach (var slider in new[] { _speed, _size, _spread, _strobeHz })
        {
            slider.ValueChanged += (_, _) => PushEffects();
        }
        foreach (var check in new[] { _mirror, _moveSync, _chaseOn, _chaseAlternate, _chaseSync, _strobeOn })
        {
            check.CheckedChanged += (_, _) => PushEffects();
        }
        foreach (var box in new[] { _beatsPerCycle, _chaseSeconds, _chaseBeats })
        {
            box.ValueChanged += (_, _) => PushEffects();
        }

        _master.ValueChanged += (_, _) =>
        {
            _masterValue.Text = $"{_master.Value * 100 / 255} %";
            if (_updating)
            {
                return;
            }
            _ctx.Engine.Master = _master.Value;
            _ctx.Show.Settings.Master = _master.Value;
        };

        _blackout.Click += (_, _) => ToggleBlackout();
        _flash.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                SetFlash(true);
            }
        };
        _flash.MouseUp += (_, _) => SetFlash(false);
        _flash.MouseCaptureChanged += (_, _) => SetFlash(false);

        _bpm.ValueChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }
            _ctx.Engine.Bpm = (double)_bpm.Value;
            _ctx.Show.Settings.Bpm = (double)_bpm.Value;
            UpdateValueLabels();
        };
        _tap.Click += (_, _) => Tap();

        // Reglages sauvegardes avec la configuration (sans la marquer "modifiee").
        _fade.ValueChanged += (_, _) => _ctx.Show.Settings.FadeSeconds = (double)_fade.Value;
        _openDimmer.CheckedChanged += (_, _) => _ctx.Show.Settings.OpenDimmerOnColor = _openDimmer.Checked;
        _autoSeconds.ValueChanged += (_, _) => _ctx.Show.Settings.AutoIntervalSeconds = (double)_autoSeconds.Value;
        _autoBeats.ValueChanged += (_, _) => _ctx.Show.Settings.AutoIntervalBeats = (double)_autoBeats.Value;
        _autoSync.CheckedChanged += (_, _) => _ctx.Show.Settings.AutoSyncToBpm = _autoSync.Checked;
        _autoOn.CheckedChanged += (_, _) =>
        {
            _autoIndex = -1;
            if (!_autoOn.Checked)
            {
                _autoStatus.Text = "";
            }
        };
    }

    // =================================================================================
    //  Rafraichissement depuis le moteur / la configuration
    // =================================================================================

    /// <summary>Tout recharger (apres ouverture d'une configuration ou modification du patch).</summary>
    public void ReloadAll()
    {
        _updating = true;
        try
        {
            var s = _ctx.Show.Settings;
            Ui.SetValue(_fade, s.FadeSeconds);
            Ui.SetValue(_autoSeconds, s.AutoIntervalSeconds);
            Ui.SetValue(_autoBeats, s.AutoIntervalBeats);
            _autoSync.Checked = s.AutoSyncToBpm;
            _openDimmer.Checked = s.OpenDimmerOnColor;
            _autoOn.Checked = false; // jamais d'enchainement automatique a l'ouverture
        }
        finally
        {
            _updating = false;
        }

        _activeSceneId = null;
        RebuildFixtures();
        RebuildScenes();
        RefreshFromEngine();
    }

    public void RebuildFixtures()
    {
        _updating = true;
        try
        {
            _fixtureList.Items.Clear();
            var selected = _ctx.Engine.GetSelection().ToHashSet();
            foreach (var fixture in _ctx.Show.Fixtures)
            {
                string label = fixture.Name + (fixture.Enabled ? "" : " (désactivé)");
                int index = _fixtureList.Items.Add(new FixtureItem(fixture.Id, label));
                _fixtureList.SetItemChecked(index, selected.Contains(fixture.Id));
            }
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Recopie l'etat du moteur dans les controles (selection, effets, master, BPM...).</summary>
    public void RefreshFromEngine()
    {
        if (IsDisposed)
        {
            return;
        }

        _updating = true;
        try
        {
            var selected = _ctx.Engine.GetSelection().ToHashSet();
            for (int i = 0; i < _fixtureList.Items.Count; i++)
            {
                _fixtureList.SetItemChecked(i, selected.Contains(((FixtureItem)_fixtureList.Items[i]).Id));
            }

            var e = _ctx.Engine.GetEffects();
            _shape.SelectedIndex = Math.Max(0, MovementShapeInfo.All.ToList().IndexOf(e.Shape));
            Ui.SetValue(_speed, (int)Math.Round(e.SpeedCyclesPerMinute));
            Ui.SetValue(_size, e.SizePercent);
            Ui.SetValue(_spread, e.SpreadPercent);
            _mirror.Checked = e.Mirror;
            _moveSync.Checked = e.MovementSyncToBpm;
            Ui.SetValue(_beatsPerCycle, e.BeatsPerCycle);
            _chaseOn.Checked = e.ColorChase;
            _chaseAlternate.Checked = e.ColorChaseAlternate;
            Ui.SetValue(_chaseSeconds, e.ColorChaseStepSeconds);
            _chaseSync.Checked = e.ColorChaseSyncToBpm;
            Ui.SetValue(_chaseBeats, e.ColorChaseBeatsPerStep);
            foreach (var check in _paletteChecks)
            {
                check.Checked = e.ColorChasePalette.Any(n => TextKey.Same(n, (string)check.Tag!));
            }
            _strobeOn.Checked = e.Strobe;
            Ui.SetValue(_strobeHz, (int)Math.Round(e.StrobeHz));

            Ui.SetValue(_master, _ctx.Engine.Master);
            Ui.SetValue(_bpm, _ctx.Engine.Bpm);
        }
        finally
        {
            _updating = false;
        }

        UpdateValueLabels();
        UpdateBlackoutButton();
        UpdateFlashButton();
        HighlightActiveScene();
    }

    private void UpdateValueLabels()
    {
        double bpm = (double)_bpm.Value;
        _speedValue.Text = _moveSync.Checked
            ? $"{bpm / (double)_beatsPerCycle.Value:0.#} /min (BPM)"
            : $"{_speed.Value} cycles/min";
        _sizeValue.Text = $"{_size.Value} %";
        _spreadValue.Text = $"{_spread.Value} %";
        _strobeValue.Text = $"{_strobeHz.Value} flashs/s";
        _masterValue.Text = $"{_master.Value * 100 / 255} %";
        _speed.Enabled = !_moveSync.Checked;
    }

    /// <summary>Envoie au moteur l'etat de tous les controles d'effets.</summary>
    private void PushEffects()
    {
        if (_updating)
        {
            return;
        }

        var effects = new LiveEffects
        {
            Shape = (_shape.SelectedItem as ShapeItem)?.Shape ?? MovementShape.None,
            SpeedCyclesPerMinute = _speed.Value,
            SizePercent = _size.Value,
            SpreadPercent = _spread.Value,
            Mirror = _mirror.Checked,
            MovementSyncToBpm = _moveSync.Checked,
            BeatsPerCycle = (double)_beatsPerCycle.Value,
            ColorChase = _chaseOn.Checked,
            ColorChaseAlternate = _chaseAlternate.Checked,
            ColorChaseStepSeconds = (double)_chaseSeconds.Value,
            ColorChaseSyncToBpm = _chaseSync.Checked,
            ColorChaseBeatsPerStep = (double)_chaseBeats.Value,
            ColorChasePalette = _paletteChecks.Where(c => c.Checked).Select(c => (string)c.Tag!).ToList(),
            Strobe = _strobeOn.Checked,
            StrobeHz = _strobeHz.Value,
        };
        _ctx.Engine.SetEffects(effects);
        UpdateValueLabels();
    }

    // =================================================================================
    //  Actions
    // =================================================================================

    private void CheckFixtures(Func<FixtureInstance, bool> predicate)
    {
        var ids = _ctx.Show.Fixtures.Where(predicate).Select(f => f.Id).ToList();
        _ctx.Engine.SetSelection(ids);
        RefreshFromEngine();
    }

    private void ApplyColor(NamedColor color)
    {
        var result = _ctx.Engine.ApplyColor(color, _openDimmer.Checked);
        _ctx.NotifyLiveStateChanged();

        if (result.Applied.Count == 0 && result.Unsupported.Count == 0)
        {
            _ctx.Status("Aucun appareil coché.");
        }
        else if (result.Unsupported.Count > 0)
        {
            _ctx.Status($"{color.Name} : appliqué à {Join(result.Applied)} ; indisponible pour {Join(result.Unsupported)} " +
                        $"(enregistre un preset « {color.Name} » sur la roue de couleurs avec le bouton « + » de l'onglet de l'appareil).");
        }
        else
        {
            _ctx.Status($"{color.Name} : appliqué à {Join(result.Applied)}.");
        }
    }

    private static string Join(IReadOnlyList<string> names) => names.Count == 0 ? "aucun" : string.Join(", ", names);

    private void ApplyToSelection(Action action, string message)
    {
        action();
        _ctx.NotifyLiveStateChanged();
        _ctx.Status(message + " (appareils cochés).");
    }

    private void ToggleBlackout()
    {
        _ctx.Engine.Blackout = !_ctx.Engine.Blackout;
        UpdateBlackoutButton();
    }

    private void UpdateBlackoutButton()
    {
        bool on = _ctx.Engine.Blackout;
        _blackout.Text = on ? "BLACKOUT ACTIF\n(Espace pour rallumer)" : "BLACKOUT  (Espace)";
        _blackout.BackColor = on ? Color.DarkRed : SystemColors.Control;
        _blackout.ForeColor = on ? Color.White : SystemColors.ControlText;
        _blackout.UseVisualStyleBackColor = !on;
    }

    private void SetFlash(bool on)
    {
        if (_ctx.Engine.Flash == on)
        {
            return;
        }
        _ctx.Engine.Flash = on;
        UpdateFlashButton();
    }

    private void UpdateFlashButton()
    {
        bool on = _ctx.Engine.Flash;
        _flash.BackColor = on ? Color.Gold : SystemColors.Control;
        _flash.UseVisualStyleBackColor = !on;
    }

    private void Tap()
    {
        double? bpm = _tapTempo.Tap(_clock.Elapsed.TotalSeconds);
        if (bpm is double value)
        {
            Ui.SetValue(_bpm, value); // declenche ValueChanged -> moteur
            _ctx.Status($"Tempo : {value:0.#} BPM");
        }
    }

    // =================================================================================
    //  Scenes et mode auto
    // =================================================================================

    public void RebuildScenes()
    {
        _sceneFlow.SuspendLayout();
        var old = _sceneFlow.Controls.Cast<Control>().ToList();
        _sceneFlow.Controls.Clear();
        foreach (var control in old)
        {
            control.Dispose();
        }

        var scenes = _ctx.Show.Scenes;
        for (int i = 0; i < scenes.Count; i++)
        {
            var scene = scenes[i];
            var button = new Button
            {
                Text = $"{i + 1}. {scene.Name}" + (scene.IncludeInAuto ? "" : "\n(hors auto)"),
                Size = new Size(150, 54),
                Tag = scene.Id,
                Margin = new Padding(4),
                UseVisualStyleBackColor = true,
            };
            button.Click += (_, _) => Recall(scene, manual: true);
            button.ContextMenuStrip = BuildSceneMenu(scene);
            _sceneFlow.Controls.Add(button);
        }

        if (scenes.Count == 0)
        {
            _sceneFlow.Controls.Add(Ui.Text("Aucune scène : règle une ambiance puis clique « Enregistrer la scène actuelle ».", color: Color.DimGray));
        }
        else
        {
            _sceneFlow.Controls.Add(Ui.Text("Clic droit sur une scène : renommer, mettre à jour, déplacer, supprimer…", color: Color.DimGray));
        }

        _sceneFlow.ResumeLayout();
        HighlightActiveScene();
    }

    private ContextMenuStrip BuildSceneMenu(Scene scene)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Rappeler", null, (_, _) => Recall(scene, manual: true));
        menu.Items.Add("Mettre à jour avec l'état actuel", null, (_, _) =>
        {
            var snapshot = _ctx.Engine.CaptureScene(scene.Name);
            scene.Values = snapshot.Values;
            scene.Effects = snapshot.Effects;
            scene.Selection = snapshot.Selection;
            _ctx.MarkDirty();
            _ctx.Status($"Scène « {scene.Name} » mise à jour.");
        });
        menu.Items.Add("Renommer…", null, (_, _) =>
        {
            string? name = InputDialog.Ask(FindForm(), "Renommer la scène", "Nouveau nom :", scene.Name);
            if (name is not null)
            {
                scene.Name = name;
                _ctx.MarkDirty();
                RebuildScenes();
            }
        });
        var inAuto = new ToolStripMenuItem("Inclure dans le mode auto") { Checked = scene.IncludeInAuto, CheckOnClick = true };
        inAuto.CheckedChanged += (_, _) =>
        {
            scene.IncludeInAuto = inAuto.Checked;
            _ctx.MarkDirty();
            BeginInvoke(new Action(RebuildScenes)); // apres fermeture du menu
        };
        menu.Items.Add(inAuto);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Déplacer à gauche", null, (_, _) => MoveScene(scene, -1));
        menu.Items.Add("Déplacer à droite", null, (_, _) => MoveScene(scene, +1));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Supprimer…", null, (_, _) =>
        {
            if (Ui.Confirm(FindForm()!, $"Supprimer la scène « {scene.Name} » ?"))
            {
                _ctx.Show.Scenes.Remove(scene);
                _ctx.MarkDirty();
                BeginInvoke(new Action(RebuildScenes));
            }
        });
        return menu;
    }

    private void MoveScene(Scene scene, int delta)
    {
        var scenes = _ctx.Show.Scenes;
        int index = scenes.IndexOf(scene);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= scenes.Count)
        {
            return;
        }
        scenes.RemoveAt(index);
        scenes.Insert(target, scene);
        _ctx.MarkDirty();
        BeginInvoke(new Action(RebuildScenes));
    }

    private void SaveCurrentScene()
    {
        string? name = InputDialog.Ask(FindForm(), "Nouvelle scène",
            "Nom de la scène (elle mémorise les valeurs de tous les appareils, les effets en cours et les appareils cochés) :",
            $"Scène {_ctx.Show.Scenes.Count + 1}");
        if (name is null)
        {
            return;
        }

        var scene = _ctx.Engine.CaptureScene(name);
        _ctx.Show.Scenes.Add(scene);
        _activeSceneId = scene.Id;
        _ctx.MarkDirty();
        RebuildScenes();
        _ctx.Status($"Scène « {name} » enregistrée (pense à enregistrer la configuration : Ctrl+S).");
    }

    private void Recall(Scene scene, bool manual)
    {
        double fade = (double)_fade.Value;
        if (!manual)
        {
            // En mode auto, le fondu ne doit pas durer plus longtemps que la scene elle-meme.
            fade = Math.Min(fade, AutoIntervalSeconds() * 0.8);
        }

        _ctx.Engine.RecallScene(scene, fade);
        _activeSceneId = scene.Id;
        _ctx.NotifyLiveStateChanged(); // -> RefreshFromEngine ici et dans les onglets d'appareils

        if (manual && _autoOn.Checked)
        {
            // Un rappel manuel pendant l'auto : l'enchainement repart de cette scene.
            var autoScenes = _ctx.Show.Scenes.Where(s => s.IncludeInAuto).ToList();
            _autoIndex = autoScenes.IndexOf(scene);
            _autoLastSwitch = _clock.Elapsed.TotalSeconds;
        }
    }

    private void HighlightActiveScene()
    {
        foreach (Control control in _sceneFlow.Controls)
        {
            if (control is Button button)
            {
                bool active = Equals(button.Tag, _activeSceneId);
                button.BackColor = active ? Color.LightSkyBlue : SystemColors.Control;
                button.UseVisualStyleBackColor = !active;
            }
        }
    }

    private double AutoIntervalSeconds() => _autoSync.Checked
        ? 60.0 / Math.Max(30, _ctx.Engine.Bpm) * (double)_autoBeats.Value
        : (double)_autoSeconds.Value;

    private void OnAutoTick()
    {
        if (!_autoOn.Checked)
        {
            return;
        }

        var scenes = _ctx.Show.Scenes.Where(s => s.IncludeInAuto).ToList();
        if (scenes.Count == 0)
        {
            _autoStatus.Text = "Aucune scène incluse dans le mode auto.";
            return;
        }

        double interval = AutoIntervalSeconds();
        double now = _clock.Elapsed.TotalSeconds;
        if (_autoIndex < 0 || now - _autoLastSwitch >= interval)
        {
            _autoIndex = (_autoIndex + 1) % scenes.Count;
            _autoLastSwitch = now;
            Recall(scenes[_autoIndex], manual: false);
        }

        var current = scenes[Math.Clamp(_autoIndex, 0, scenes.Count - 1)];
        _autoStatus.Text = $"En cours : « {current.Name} » — suivante dans {Math.Max(0, interval - (now - _autoLastSwitch)):0.0} s";
    }

    // =================================================================================
    //  Clavier (appele par MainForm quand cet onglet est affiche)
    // =================================================================================

    /// <returns>true si la touche a ete utilisee (elle ne doit alors pas aller au controle qui a le focus).</returns>
    public bool HandleKey(Keys key, bool down)
    {
        bool known = key is Keys.Space or Keys.F or Keys.T
                     || key is >= Keys.D1 and <= Keys.D9
                     || key is >= Keys.NumPad1 and <= Keys.NumPad9;
        if (!known)
        {
            return false;
        }

        if (!down)
        {
            _heldKeys.Remove(key);
            if (key == Keys.F)
            {
                SetFlash(false);
            }
            return true;
        }

        // L'auto-repetition du clavier envoie des KeyDown en rafale : on n'agit qu'au premier.
        if (!_heldKeys.Add(key))
        {
            return true;
        }

        switch (key)
        {
            case Keys.Space:
                ToggleBlackout();
                break;
            case Keys.F:
                SetFlash(true);
                break;
            case Keys.T:
                Tap();
                break;
            default:
                int number = key >= Keys.NumPad1 ? key - Keys.NumPad1 + 1 : key - Keys.D1 + 1;
                if (number <= _ctx.Show.Scenes.Count)
                {
                    Recall(_ctx.Show.Scenes[number - 1], manual: true);
                }
                break;
        }
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ctx.LiveStateChanged -= RefreshFromEngine;
            _autoTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
