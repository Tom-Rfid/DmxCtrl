// =====================================================================================
//  FixturePanel.cs
// -------------------------------------------------------------------------------------
//  Tab of ONE fixture, generated automatically from its profile:
//    - header: name, profile, address range, warning if channels are unchecked
//    - XY pad to aim the moving head (if the profile has pan/tilt)
//    - "wings" pad for a fixture with 2 motors (Spider): left/right = position
//      of both wings, up/down = dimmer; "Invert" box = mirrored wings
//    - one row per channel: 0-255 slider, value, preset list,
//      "+" button (save the current value as a preset), "OK" box (checked)
//
//  The "+" button is the simplest way to complete a profile on site: move the
//  colour wheel slider to red, "+", type "Rouge" -> the Red button of the Show
//  tab then works for every moving head of this model.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Engine;
using DmxController.Core.Model;

namespace DmxController.UI;

/// <summary>Generic tab of one fixture (moving head, Spider, PAR...), built from its profile.</summary>
[DesignerCategory("Code")]
internal sealed class FixturePanel : UserControl
{
    /// <summary>Item of a preset list: name and value.</summary>
    private sealed record PresetItem(string Name, byte Value)
    {
        /// <summary>Text shown in the list: translated name and value.</summary>
        public override string ToString() => $"{Tr.T(Name)} ({Value})";
    }

    private const string NoPreset = "— preset —";

    private readonly ShowContext _ctx;
    private readonly FixtureInstance _fixture;
    private readonly FixtureProfile _profile;
    private readonly TrackBar[] _sliders;
    private readonly NumericUpDown[] _values; // editable value (and + / - buttons) next to each slider
    private readonly Label[] _names;
    private readonly ComboBox[] _presets;
    private readonly CheckBox[] _verified;
    private readonly XYPad? _xy;
    private readonly XYPad? _wings;
    private readonly CheckBox? _mirrorWings;
    private readonly Label? _wingsInfo;
    private readonly int[] _motors;   // indexes of the Motor channels in the profile
    private readonly int _dimmer;     // index of the Dimmer channel (-1 if none)

    // "Left / right LED" faders (fixtures with wings): groups of colour channels, and the
    // remembered ratios of each group to get the colour back after going through 0.
    private readonly VerticalFader? _ledLeft;
    private readonly VerticalFader? _ledRight;
    private readonly int[] _groupLeft = Array.Empty<int>();
    private readonly int[] _groupRight = Array.Empty<int>();
    private readonly double[]?[] _groupRatios = new double[]?[2];

    private readonly CheckBox? _strobeOn; // "Strobe" box (enabled / disabled), when a channel can strobe
    private readonly int _strobeIndex = -1;

    // Left <-> right effect of the wings (fixtures with 2 motors)
    private readonly CheckBox? _sweep;
    private readonly NumericUpDown? _sweepDelay;
    private readonly NumericUpDown? _sweepAmplitude;
    private readonly CheckBox? _sweepSync;
    private readonly NumericUpDown? _sweepBeats;
    private readonly CheckBox? _micListen;   // listen to the microphone (shared with the Show tab)
    private readonly Label? _micBpm;
    private readonly CheckBox? _micFollow;   // "mic tempo" mode
    private readonly CheckBox? _modeDelay;   // "delay in seconds" mode
    private readonly NumericUpDown? _bpmBox; // "tempo in BPM" mode (= BPM of the Show tab)
    private bool _micUpdating;

    /// <summary>Rate source of the left/right effect: fixed delay, typed BPM or microphone tempo.</summary>
    private enum SweepMode { Delay, Bpm, Mic }

    // Colour mask (All, R, G, B, White): ticked boxes = colours allowed on output.
    private readonly CheckBox? _maskAll;
    private readonly List<CheckBox> _maskBoxes = new();
    private bool _maskUpdating;
    private readonly Label _warning = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 6, 3, 3) };
    private readonly ToolTip _tips = new();
    private bool _updating;
    private bool _numberUpdating; // numeric box updated by code (not by the user)
    private readonly BeatEffectBlock? _beatBlock; // beat effect (fixtures without motors)
    private readonly GroupLevelPad? _groupPad;    // chart of groups A, B, C, All

    // Banner shown while the fixture plays an internal program (Mode channel not on "Manual").
    private readonly Label _modeLock = new()
    {
        Text = Tr.T("Mode programme interne : dimmer, groupes, strobe et effet au tempo sont inactifs. ") +
               Tr.T("Choisis « Manuel (DMX direct) » dans le canal Mode pour les retrouver."),
        AutoSize = true,
        MaximumSize = new Size(1000, 0),
        Font = Ui.BoldFont,
        ForeColor = Ui.WarningColor,
        Visible = false,
        Margin = new Padding(3, 4, 3, 4),
    };
    private readonly VerticalFader? _groupsFader; // the 3 groups together
    private readonly int[] _groups = Array.Empty<int>(); // indexes of the LED group channels
    private double[]? _groupsRatios;

    /// <summary>Builds the tab of <paramref name="fixture"/>: header, pads, effect blocks and one row per channel.</summary>
    public FixturePanel(ShowContext ctx, FixtureInstance fixture, FixtureProfile profile)
    {
        _ctx = ctx;
        _fixture = fixture;
        _profile = profile;
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.None;

        int count = profile.ChannelCount;
        _sliders = new TrackBar[count];
        _values = new NumericUpDown[count];
        _names = new Label[count];
        _presets = new ComboBox[count];
        _verified = new CheckBox[count];
        _motors = Enumerable.Range(0, count).Where(i => profile.Channels[i].Type == ChannelType.Motor).ToArray();
        _dimmer = profile.IndexOf(ChannelType.Dimmer);

        // ---------------- Header ----------------
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 4),
        };
        header.Controls.Add(new Label
        {
            Text = $"{fixture.Name}  —  {profile.Manufacturer} {Tr.T(profile.Name)}".Trim(),
            AutoSize = true,
            Font = Ui.TitleFont,
        });
        header.Controls.Add(Ui.Text(
            Tr.F("Adresse DMX {0} : canaux {1} à {2}", fixture.Address, fixture.Address, fixture.EndAddress(profile)) +
            (fixture.Enabled ? "" : Tr.T("   —   APPAREIL DÉSACTIVÉ dans le patch (rien n'est envoyé)")),
            color: fixture.Enabled ? Color.DimGray : Color.DarkRed));
        header.Controls.Add(_warning);
        if (!string.IsNullOrWhiteSpace(profile.Notes))
        {
            header.Controls.Add(new Label
            {
                Text = Tr.T(profile.Notes),
                AutoSize = true,
                MaximumSize = new Size(1000, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 2, 3, 6),
            });
        }

        // ---------------- XY pad (moving heads) ----------------
        Panel? left = null;
        if (profile.CanMove)
        {
            left = new Panel { Dock = DockStyle.Left, Width = 290, Padding = new Padding(10) };
            _xy = new XYPad { Location = new Point(12, 6), Size = new Size(260, 260) };
            _xy.ValueChanged += OnXyChanged;

            var center = new Button { Text = Tr.T("Centrer"), Location = new Point(12, 274), Width = 126, Height = 30 };
            center.Click += (_, _) =>
            {
                _ctx.Engine.SetPanTilt16(_fixture.Id, 32768, 32768);
                RefreshFromEngine();
            };
            var reset = new Button { Text = Tr.T("Par défaut"), Location = new Point(146, 274), Width = 126, Height = 30 };
            reset.Click += (_, _) => ResetDefaults();

            var hint = new Label
            {
                Text = Tr.T("Clique / glisse dans le carré pour orienter la lyre.\n") +
                       Tr.T("Les effets de mouvement de l'onglet Soirée tournent autour de cette position."),
                Location = new Point(12, 312),
                Size = new Size(260, 60),
                ForeColor = Color.DimGray,
            };
            left.Controls.AddRange(new Control[] { _xy, center, reset, hint });
        }
        else if (_motors.Length >= 2)
        {
            // Fixture with 2 motors (Spider wings): X = wing position, Y = dimmer (top = 100 %),
            // and right of the pad, one vertical fader per wing for its LEDs.
            left = new Panel { Dock = DockStyle.Left, Width = 400, Padding = new Padding(10), AutoScroll = true };

            (_groupLeft, _groupRight) = LedGroups.Split(profile);
            if (_groupLeft.Length > 0 && _groupRight.Length > 0)
            {
                _ledLeft = new VerticalFader { Location = new Point(284, 6), Size = new Size(48, 200) };
                _ledRight = new VerticalFader { Location = new Point(340, 6), Size = new Size(48, 200) };
                _ledLeft.ValueChanged += (_, _) => OnLedFader(0);
                _ledRight.ValueChanged += (_, _) => OnLedFader(1);
                _tips.SetToolTip(_ledLeft, Tr.F("LED de l'aile gauche ({0}) : bas = 0 %, haut = 100 %.\nGarde la couleur en cours (les proportions entre LED).", LedNames(_groupLeft)));
                _tips.SetToolTip(_ledRight, Tr.F("LED de l'aile droite ({0}) : bas = 0 %, haut = 100 %.\nGarde la couleur en cours (les proportions entre LED).", LedNames(_groupRight)));
                left.Controls.Add(_ledLeft);
                left.Controls.Add(_ledRight);
                left.Controls.Add(new Label { Text = Tr.T("LED\ngauche"), Location = new Point(276, 210), Size = new Size(64, 46), TextAlign = ContentAlignment.TopCenter });
                left.Controls.Add(new Label { Text = Tr.T("LED\ndroite"), Location = new Point(332, 210), Size = new Size(64, 46), TextAlign = ContentAlignment.TopCenter });
            }
            _wings = new XYPad
            {
                Location = new Point(12, 6),
                Size = new Size(260, 200),
                XLabel = Tr.T("Ailes ← gauche · droite →"),
                YLabel = _dimmer >= 0 ? Tr.T("Dimmer : haut = 100 %, bas = 0") : "",
            };
            _wings.ValueChanged += OnWingsChanged;

            _mirrorWings = new CheckBox
            {
                Text = Tr.T("Inverser (ailes en miroir)"),
                Checked = fixture.MirrorMotors,
                Location = new Point(12, 240),
                AutoSize = true,
            };
            _mirrorWings.CheckedChanged += (_, _) => OnMirrorChanged();

            var center = new Button { Text = Tr.T("Ailes au milieu"), Location = new Point(12, 266), Width = 126, Height = 30 };
            center.Click += (_, _) =>
            {
                _ctx.Engine.SetMotors(_fixture.Id, 128, _mirrorWings.Checked);
                RefreshFromEngine();
            };
            var reset = new Button { Text = Tr.T("Par défaut"), Location = new Point(146, 266), Width = 126, Height = 30 };
            reset.Click += (_, _) => ResetDefaults();

            _wingsInfo = new Label { Location = new Point(12, 302), Size = new Size(260, 20), Font = Ui.BoldFont };
            _tips.SetToolTip(_wings, Tr.T("Gauche / droite : les 2 ailes (opposées avec « Inverser »). Haut / bas : dimmer."));
            left.Controls.AddRange(new Control[] { _wings, _mirrorWings, center, reset, _wingsInfo });

            // Colour mask: an unticked colour is forced to 0 on the DMX output, whatever happens.
            var maskRow = new FlowLayoutPanel
            {
                Location = new Point(9, 210),
                Size = new Size(265, 26),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, // otherwise it stays 100 px high and hides what follows
                WrapContents = false,
                Margin = new Padding(0),
            };
            _maskAll = new CheckBox { Text = Tr.T("All"), AutoSize = true, Font = Ui.BoldFont };
            maskRow.Controls.Add(_maskAll);
            var colors = new (ChannelType Type, string Label, Color Color)[]
            {
                (ChannelType.Red, "R", Color.Red), (ChannelType.Green, "G", Color.Green),
                (ChannelType.Blue, "B", Color.Blue), (ChannelType.White, Tr.T("White"), SystemColors.ControlText),
            };
            foreach (var (type, label, color) in colors)
            {
                if (!profile.Has(type))
                {
                    continue;
                }
                var box = new CheckBox { Text = label, AutoSize = true, Tag = type, ForeColor = color, Font = Ui.BoldFont, Checked = !fixture.MutedColors.Contains(type) };
                box.CheckedChanged += (_, _) => OnMaskChanged();
                _tips.SetToolTip(box, Tr.F("Décoché : toutes les LED {0} sont coupées (0) en sortie, même pendant un flash ou un chenillard.", label));
                _maskBoxes.Add(box);
                maskRow.Controls.Add(box);
            }
            _maskAll.Checked = _maskBoxes.All(b => b.Checked);
            _maskAll.CheckedChanged += (_, _) => OnMaskAllChanged();
            _tips.SetToolTip(_maskAll, Tr.T("Coche / décoche toutes les couleurs."));
            left.Controls.Add(maskRow);

            // Automatic left <-> right effect with a delay (follows the "Invert" box).
            // A table (not fixed positions): labels take the room they need, even with
            // Windows display scaling (125 %, 150 %...).
            _sweep = new CheckBox
            {
                Text = Tr.T("Effet gauche ↔ droite"),
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = Ui.BoldFont,
                Size = new Size(260, 32),
                Checked = ctx.Engine.IsWingSweeping(fixture.Id),
            };
            _sweep.CheckedChanged += (_, _) => OnSweepToggled();
            _sweepDelay = new NumericUpDown { Minimum = 0.2m, Maximum = 60, DecimalPlaces = 1, Increment = 0.1m, Width = 70 };
            _sweepAmplitude = new NumericUpDown { Minimum = 5, Maximum = 100, Increment = 5, Width = 70 };
            // 3 mutually exclusive modes: fixed delay (s), tempo typed in BPM, tempo detected by the mic.
            _modeDelay = new CheckBox { Text = Tr.T("Délai"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            _sweepSync = new CheckBox { Text = Tr.T("Tempo"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            _bpmBox = new NumericUpDown { Minimum = 30, Maximum = 300, DecimalPlaces = 1, Increment = 1, Width = 70 };
            _sweepBeats = new NumericUpDown { Minimum = 0.25m, Maximum = 64, DecimalPlaces = 2, Increment = 0.25m, Width = 70 };
            Ui.SetValue(_sweepDelay, fixture.WingSweepDelaySeconds);
            Ui.SetValue(_sweepAmplitude, fixture.WingSweepAmplitudePercent);
            Ui.SetValue(_sweepBeats, fixture.WingSweepBeats);
            Ui.SetValue(_bpmBox, ctx.Engine.Bpm);
            _sweepDelay.ValueChanged += (_, _) => SaveSweepSettings();
            _sweepAmplitude.ValueChanged += (_, _) => SaveSweepSettings();
            _sweepBeats.ValueChanged += (_, _) => SaveSweepSettings();
            _modeDelay.CheckedChanged += (_, _) => OnModeChecked(SweepMode.Delay, _modeDelay);
            _sweepSync.CheckedChanged += (_, _) => OnModeChecked(SweepMode.Bpm, _sweepSync);
            _bpmBox.ValueChanged += (_, _) => OnBpmTyped();

            // Microphone: the same one as the Show tab (_ctx.Mic).
            _micListen = new CheckBox
            {
                Text = Tr.T("Micro"),
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(110, 28),
                Enabled = Audio.MicController.DeviceNames().Count > 0,
            };
            _micListen.CheckedChanged += (_, _) => OnMicListenToggled();
            _micBpm = new Label { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 8, 3, 3) };
            _micFollow = new CheckBox { Text = Tr.T("Tempo du micro"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            _micFollow.CheckedChanged += (_, _) => OnModeChecked(SweepMode.Mic, _micFollow);

            _tips.SetToolTip(_sweep, Tr.T("Les ailes vont à gauche, attendent le délai, vont à droite, attendent… ") +
                                     Tr.T("Avec « Inverser », l'aile 2 fait le mouvement opposé."));
            _tips.SetToolTip(_modeDelay, Tr.T("Un aller toutes les N secondes."));
            _tips.SetToolTip(_sweepSync, Tr.T("Tempo saisi en BPM (le même que l'onglet Soirée / tap tempo) : 1 aller = N temps."));
            _tips.SetToolTip(_micListen, Tr.T("Écoute le micro pour détecter le tempo (le même micro que l'onglet Soirée)."));
            _tips.SetToolTip(_micFollow, Tr.T("Le BPM suit le tempo détecté au micro, et l'effet gauche/droite se cale dessus."));

            var effect = new TableLayoutPanel
            {
                Location = new Point(9, 326),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            };
            for (int c = 0; c < 3; c++)
            {
                effect.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            }
            void Row(Control first, Control? second = null, string unit = "")
            {
                int row = effect.RowCount++;
                effect.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                effect.Controls.Add(first, 0, row);
                if (second is null)
                {
                    effect.SetColumnSpan(first, 3);
                    return;
                }
                effect.Controls.Add(second, 1, row);
                if (unit.Length > 0)
                {
                    effect.Controls.Add(Ui.Text(unit), 2, row);
                }
            }
            Row(_sweep);
            Row(_modeDelay, _sweepDelay, Tr.T("s"));
            Row(_sweepSync, _bpmBox, Tr.T("BPM"));
            Row(_micFollow, _micListen);
            Row(_micBpm);
            Row(Ui.Text(Tr.T("1 aller (tempo) =")), _sweepBeats, Tr.T("temps"));
            Row(Ui.Text(Tr.T("Amplitude")), _sweepAmplitude, Tr.T("%"));
            left.Controls.Add(effect);
            ShowMode(CurrentMode());

            _ctx.Mic.Changed += UpdateMicUi;
            UpdateMicUi();
        }
        else
        {
            var reset = new Button { Text = Tr.T("Valeurs par défaut"), AutoSize = true };
            reset.Click += (_, _) => ResetDefaults();
            header.Controls.Add(reset);
            header.Controls.Add(_modeLock);

            // Fixture with several LED groups (UV PAR, RGB PAR...): beat effect on the left.
            if (profile.Channels.Count(c => ChannelTypeInfo.IsColorMix(c.Type)) >= 2)
            {
                left = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(10), AutoScroll = true };

                // Group chart (A, B, C, All) + fader for the 3 groups together + ALL OFF.
                _groups = Enumerable.Range(0, count).Where(i => ChannelTypeInfo.IsColorMix(profile.Channels[i].Type)).ToArray();
                _groupPad = new GroupLevelPad
                {
                    Location = new Point(9, 6),
                    Size = new Size(240, 180),
                    GroupNames = _groups.Select((_, k) => ((char)('A' + k)).ToString()).ToArray(),
                };
                _groupPad.LevelChanged += OnGroupPad;
                _tips.SetToolTip(_groupPad, Tr.T("X : groupe A, B, C ou Tous. Y : niveau (bas = 0 %, haut = 100 %). Clique ou glisse."));
                _groupsFader = new VerticalFader { Location = new Point(258, 6), Size = new Size(48, 180), FillColor = Color.FromArgb(160, 100, 240) };
                _groupsFader.ValueChanged += (_, _) => OnGroupsFader();
                _tips.SetToolTip(_groupsFader, Tr.T("Les 3 groupes en même temps (garde leurs proportions)."));
                var faderLabel = new Label { Text = Tr.T("3 groupes"), Location = new Point(250, 188), Size = new Size(64, 18), TextAlign = ContentAlignment.TopCenter };
                var cutAll = new Button
                {
                    Text = Tr.T("TOUT COUPER"),
                    Location = new Point(9, 210),
                    Size = new Size(297, 34),
                    Font = Ui.BoldFont,
                    BackColor = Color.FromArgb(190, 40, 40),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                };
                cutAll.Click += (_, _) => CutAll();
                _tips.SetToolTip(cutAll, Tr.T("Dimmer, groupes et strobe à 0, effet au tempo arrêté."));

                _beatBlock = new BeatEffectBlock(ctx, fixture, profile) { Location = new Point(9, 252) };
                left.Controls.AddRange(new Control[] { _groupPad, _groupsFader, faderLabel, cutAll, _beatBlock });
            }
        }

        // ---------------- One row per channel ----------------
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 7,
            Location = new Point(6, 4),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        for (int c = 0; c < 7; c++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        for (int i = 0; i < count; i++)
        {
            int index = i; // captured for the lambdas
            var channel = profile.Channels[i];

            _names[i] = new Label { AutoSize = true, MinimumSize = new Size(260, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            _tips.SetToolTip(_names[i], $"{ChannelTypeInfo.DisplayName(channel.Type)}" +
                                        (string.IsNullOrWhiteSpace(channel.Notes) ? "" : $"\n{Tr.T(channel.Notes)}"));

            _sliders[i] = Ui.Slider(0, 255, left is null ? 330 : 230); // narrower when a pad uses the left side
            _sliders[i].ValueChanged += (_, _) => OnSlider(index);

            _values[i] = new NumericUpDown { Minimum = 0, Maximum = 255, Width = 58, Margin = new Padding(3, 4, 3, 3) }; _values[i].ValueChanged += (_, _) => OnNumberTyped(index);

            _presets[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, DropDownWidth = 260, Margin = new Padding(3, 4, 3, 3) };
            _presets[i].SelectionChangeCommitted += (_, _) => OnPresetChosen(index);

            var add = new Button { Text = "+", Width = 28, Height = 26, Margin = new Padding(3, 3, 3, 3) };
            _tips.SetToolTip(add, Tr.T("Enregistrer la valeur actuelle comme preset (ex. « Rouge » sur la roue de couleurs)"));
            add.Click += (_, _) => AddPreset(index);

            _verified[i] = new CheckBox { Text = Tr.T("OK"), AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
            _tips.SetToolTip(_verified[i], Tr.T("Coche quand tu as vérifié sur l'appareil que ce canal fait bien ce qui est indiqué"));
            _verified[i].CheckedChanged += (_, _) => OnVerifiedChanged(index);

            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(_names[i], 0, i);
            table.Controls.Add(_sliders[i], 1, i);
            table.Controls.Add(_values[i], 2, i);
            table.Controls.Add(_presets[i], 3, i);
            table.Controls.Add(add, 4, i);
            table.Controls.Add(_verified[i], 5, i);

            // Next to the (first) channel that can strobe (Strobe channel, shutter of the 260X...):
            // "Strobe" box (enabled / disabled, remembering the value) + "Strobe 0" button.
            if (channel.IsStrobeCapable && _strobeOn is null)
            {
                _strobeIndex = i;
                _strobeOn = new CheckBox
                {
                    Text = Tr.T("Strobe"),
                    AutoSize = true,
                    Checked = !fixture.NoStrobe,
                    Font = Ui.BoldFont,
                    Margin = new Padding(10, 6, 3, 3),
                };
                _strobeOn.CheckedChanged += (_, _) => OnStrobeToggled();
                var strobeZero = new Button { Text = Tr.T("Strobe 0"), AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
                strobeZero.Click += (_, _) => SetStrobeFixed();
                _tips.SetToolTip(_strobeOn, Tr.T("Décoché : strobe désactivé (canal fixe, la valeur est mémorisée ; rien ne peut le faire strober). ") +
                                            Tr.T("Recoché : l'ancienne valeur revient. Bouger la barre dans une valeur de strobe le réactive."));
                _tips.SetToolTip(strobeZero, channel.Type == ChannelType.Strobe
                    ? Tr.T("Met le strobe à 0 (pas de strobe).")
                    : Tr.F("Remet l'obturateur en fixe (ouvert, {0}) : plus de strobe.", channel.NoStrobeValue));
                table.Controls.Add(Ui.Flow(0, _strobeOn, strobeZero), 6, i);
            }
        }
        scroll.Controls.Add(table);

        // Order of adding: the "Fill" area first, then the edges.
        Controls.Add(scroll);
        if (left is not null)
        {
            Controls.Add(left);
        }
        Controls.Add(header);

        _ctx.LiveStateChanged += RefreshFromEngine;
        _ctx.ProfileDetailsChanged += RefreshProfileDetails;

        RefreshProfileDetails();
        RefreshFromEngine();
    }

    /// <summary>Id of the fixture shown by this tab.</summary>
    public string FixtureId => _fixture.Id;

    /// <summary>Copies the engine values into the sliders (after a scene recall, a colour...).</summary>
    public void RefreshFromEngine()
    {
        if (IsDisposed)
        {
            return;
        }

        byte[] values = _ctx.Engine.GetValues(_fixture.Id);
        _updating = true;
        try
        {
            for (int i = 0; i < _sliders.Length && i < values.Length; i++)
            {
                _sliders[i].Value = values[i];
                ShowNumber(i, values[i]);
                SyncPresetCombo(i, values[i]);
            }
            SyncPads(values);
        }
        finally
        {
            _updating = false;
        }
        UpdateMicUi(); // effect BPM (tap tempo of the Show tab...)
        _beatBlock?.UpdateUi();
    }

    /// <summary>Refreshes presets, "OK" boxes, "(?)" labels and the warning.</summary>
    public void RefreshProfileDetails()
    {
        if (IsDisposed)
        {
            return;
        }

        _updating = true;
        try
        {
            for (int i = 0; i < _profile.ChannelCount && i < _sliders.Length; i++)
            {
                var channel = _profile.Channels[i];
                _names[i].Text = Tr.F("{0}. {1}   [DMX {2}]", (i + 1), Tr.T(channel.Name), (_fixture.Address + i)) + (channel.Verified ? "" : "  (?)");
                _names[i].ForeColor = channel.Verified ? SystemColors.ControlText : Ui.WarningColor;
                _verified[i].Checked = channel.Verified;

                var combo = _presets[i];
                combo.Items.Clear();
                combo.Items.Add(NoPreset);
                foreach (var preset in channel.Presets)
                {
                    combo.Items.Add(new PresetItem(preset.Name, preset.Value));
                }
                combo.Enabled = channel.Presets.Count > 0;
                SyncPresetCombo(i, (byte)_sliders[i].Value);
            }

            int unverified = _profile.UnverifiedCount;
            _warning.Text = unverified == 0
                ? Tr.T("Profil entièrement vérifié.")
                : Tr.F("Profil à vérifier : {0} canal(aux) non confirmé(s), marqués (?). ", unverified) +
                  Tr.T("Teste-les (curseur ou onglet Debug) puis coche « OK ».");
            _warning.ForeColor = unverified == 0 ? Ui.OkColor : Ui.WarningColor;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Chart: column of a group -> that group; last column -> all 3 at the same level.</summary>
    private void OnGroupPad(int column, byte level)
    {
        OpenDimmerIfClosed();
        if (column >= _groups.Length)
        {
            foreach (int i in _groups)
            {
                _ctx.Engine.SetValue(_fixture.Id, i, level);
            }
        }
        else if (column >= 0)
        {
            _ctx.Engine.SetValue(_fixture.Id, _groups[column], level);
        }
        RefreshFromEngine();
    }

    /// <summary>"3 groups" fader: common level, keeping the ratios between groups.</summary>
    private void OnGroupsFader()
    {
        if (_updating || _groupsFader is null)
        {
            return;
        }
        OpenDimmerIfClosed();
        _ctx.Engine.SetGroupLevel(_fixture.Id, _groups, (byte)_groupsFader.Value, _groupsRatios);
        RefreshFromEngine();
    }

    /// <summary>The master dimmer must be open, otherwise setting a group would show nothing.</summary>
    private void OpenDimmerIfClosed()
    {
        byte[] values = _ctx.Engine.GetValues(_fixture.Id);
        if (_dimmer >= 0 && _dimmer < values.Length && values[_dimmer] == 0)
        {
            _ctx.Engine.SetFixtureDimmer(_fixture.Id, 255);
        }
    }

    /// <summary>
    /// Fixture mode (Mode channel): outside "Manual" it plays its internal programs and ignores
    /// dimmer, groups and strobe -> these settings, the chart and the beat effect are greyed out
    /// (and the effect is stopped). The Mode channel (with its presets) and the speed stay usable.
    /// </summary>
    private void UpdateManualLock(byte[] values)
    {
        if (_profile.ManualModeRange() is null)
        {
            return;
        }
        bool manual = _profile.IsInManualMode(values);
        for (int i = 0; i < _profile.ChannelCount && i < _sliders.Length; i++)
        {
            var type = _profile.Channels[i].Type;
            if (type is ChannelType.Dimmer or ChannelType.Strobe || ChannelTypeInfo.IsColorMix(type))
            {
                _sliders[i].Enabled = manual;
                _values[i].Enabled = manual;
                _presets[i].Enabled = manual && _profile.Channels[i].Presets.Count > 0;
            }
        }
        if (_strobeOn?.Parent is Control strobeRow)
        {
            strobeRow.Enabled = manual; // "Strobe" box + "Strobe 0" button
        }
        if (_groupPad is not null && _groupsFader is not null)
        {
            _groupPad.Enabled = manual;
            _groupsFader.Enabled = manual;
        }
        if (_beatBlock is not null)
        {
            if (!manual && _beatBlock.Enabled)
            {
                _beatBlock.Stop(); // an internal program replaces the effect
            }
            _beatBlock.Enabled = manual;
        }
        _modeLock.Visible = !manual;
    }

    /// <summary>ALL OFF: beat effect stopped, manual mode, dimmer, groups and strobe at 0.</summary>
    private void CutAll()
    {
        _beatBlock?.Stop();
        _ctx.Engine.SetFixtureStrobe(_fixture.Id, false);
        if (_profile.ManualModeRange() is { } mode)
        {
            _ctx.Engine.SetValue(_fixture.Id, mode.Index, mode.Manual.From); // stops the internal programs
        }
        for (int i = 0; i < _profile.ChannelCount; i++)
        {
            var type = _profile.Channels[i].Type;
            if (type is ChannelType.Dimmer or ChannelType.Strobe || ChannelTypeInfo.IsColorMix(type))
            {
                _ctx.Engine.SetValue(_fixture.Id, i, 0);
            }
        }
        _ctx.NotifyLiveStateChanged();
        _ctx.Status(Tr.F("{0} : tout coupé.", _fixture.Name));
    }

    /// <summary>Group chart and fader realigned on the current values.</summary>
    private void SyncGroups(byte[] values)
    {
        if (_groupPad is null || _groupsFader is null || !_groups.All(i => i < values.Length))
        {
            return;
        }
        _groupPad.SetLevels(_groups.Select(i => values[i]).ToArray());
        _groupsFader.Value = LedGroups.Level(values, _groups);
        if (LedGroups.Level(values, _groups) >= 32)
        {
            _groupsRatios = LedGroups.Ratios(values, _groups); // remembered ratios (see RememberRatios)
        }
    }

    /// <summary>Shows a value in the numeric box without raising an event again.</summary>
    private void ShowNumber(int index, byte value)
    {
        _numberUpdating = true;
        try
        {
            _values[index].Value = value;
        }
        finally
        {
            _numberUpdating = false;
        }
    }

    /// <summary>Typed value (or + / -): the slider follows, and the slider is what sends it to the engine.</summary>
    private void OnNumberTyped(int index)
    {
        if (_numberUpdating || _updating)
        {
            return;
        }
        _sliders[index].Value = (int)_values[index].Value; // -> OnSlider
    }

    /// <summary>A channel slider moved: sends the value to the engine and updates the related controls (pads, presets, strobe box).</summary>
    private void OnSlider(int index)
    {
        if (_updating)
        {
            return;
        }

        byte value = (byte)_sliders[index].Value;
        ShowNumber(index, value);
        _ctx.Engine.SetValue(_fixture.Id, index, value);
        SyncPresetCombo(index, value);
        ReactivateStrobeIfNeeded(index, value);

        var type = _profile.Channels[index].Type;
        if (type is ChannelType.Pan or ChannelType.PanFine or ChannelType.Tilt or ChannelType.TiltFine
            or ChannelType.Motor or ChannelType.Dimmer or ChannelType.Macro || ChannelTypeInfo.IsColorMix(type))
        {
            SyncPads(_ctx.Engine.GetValues(_fixture.Id)); // Macro: manual mode / program -> greyed out or not
        }
    }

    /// <summary>Realigns the pads (moving head and wings) on the current channel values.</summary>
    private void SyncPads(byte[] values)
    {
        SyncGroups(values);
        UpdateManualLock(values);
        if (_xy is not null)
        {
            _xy.ValueX = ShowEngine.GetAxis16(_profile, values, ChannelType.Pan, ChannelType.PanFine);
            _xy.ValueY = ShowEngine.GetAxis16(_profile, values, ChannelType.Tilt, ChannelType.TiltFine);
        }

        if (_wings is not null && _wingsInfo is not null && _motors.Length >= 2 && _motors[1] < values.Length)
        {
            byte wing1 = values[_motors[0]];
            byte wing2 = values[_motors[1]];
            _wings.ValueX = wing1 * 257; // 0..255 -> 0..65535
            if (_dimmer >= 0 && _dimmer < values.Length)
            {
                _wings.ValueY = (255 - values[_dimmer]) * 257; // top of the pad = dimmer full
            }
            _wingsInfo.Text = Tr.F("Aile 1 : {0}°   Aile 2 : {1}°", Degrees(wing1), Degrees(wing2)) +
                              (_dimmer >= 0 && _dimmer < values.Length ? Tr.F("   Dimmer : {0} %", (values[_dimmer] * 100 / 255)) : "");
        }

        if (_ledLeft is not null && _ledRight is not null && _groupRight.All(i => i < values.Length))
        {
            _ledLeft.Value = LedGroups.Level(values, _groupLeft);
            _ledRight.Value = LedGroups.Level(values, _groupRight);
            RememberRatios(0, values, _groupLeft);
            RememberRatios(1, values, _groupRight);
        }
    }

    /// <summary>
    /// Remembers the ratios of an LED group while they are bright enough to be accurate:
    /// that way a red wing taken down to 0 and back up is red again.
    /// </summary>
    private void RememberRatios(int side, byte[] values, int[] group)
    {
        if (LedGroups.Level(values, group) >= 32)
        {
            _groupRatios[side] = LedGroups.Ratios(values, group);
        }
    }

    /// <summary>Left or right LED fader moved: sets the level of that wing's LED group, keeping the colour ratios.</summary>
    private void OnLedFader(int side)
    {
        if (_updating)
        {
            return;
        }

        var fader = side == 0 ? _ledLeft! : _ledRight!;
        var group = side == 0 ? _groupLeft : _groupRight;
        _ctx.Engine.SetGroupLevel(_fixture.Id, group, (byte)fader.Value, _groupRatios[side]);

        // Updates the LED sliders without reading back the position of the fader being dragged.
        byte[] values = _ctx.Engine.GetValues(_fixture.Id);
        _updating = true;
        try
        {
            foreach (int i in group)
            {
                _sliders[i].Value = values[i];
                ShowNumber(i, values[i]);
            }
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Names of the channels of an LED group (for tooltips).</summary>
    private string LedNames(int[] group) => string.Join(", ", group.Select(i => _profile.Channels[i].Name));

    /// <summary>
    /// "Strobe" box: unticked -> the current value is remembered, the channel goes back to fixed and
    /// the engine blocks any strobe (FixtureInstance.NoStrobe); ticked -> the old value comes back.
    /// </summary>
    private void OnStrobeToggled()
    {
        if (_updating || _strobeOn is null || _strobeIndex < 0)
        {
            return;
        }

        var channel = _profile.Channels[_strobeIndex];
        bool on = _strobeOn.Checked;
        byte current = _ctx.Engine.GetValues(_fixture.Id).ElementAtOrDefault(_strobeIndex);
        _ctx.Engine.EditShow(_ =>
        {
            _fixture.NoStrobe = !on;
            if (!on && channel.IsStrobeValue(current))
            {
                _fixture.StrobeMemory = current; // to get it back when ticking again
            }
        });
        if (on)
        {
            if (channel.IsStrobeValue(_fixture.StrobeMemory))
            {
                _ctx.Engine.SetValue(_fixture.Id, _strobeIndex, _fixture.StrobeMemory);
            }
        }
        else
        {
            _ctx.Engine.SetValue(_fixture.Id, _strobeIndex, channel.NoStrobeValue);
        }
        _ctx.MarkDirty();
        _ctx.NotifyLiveStateChanged(); // this panel + the Show tab (STROBE switch)
        _ctx.Status(on ? Tr.F("{0} : strobe réactivé ({1}).", _fixture.Name, _fixture.StrobeMemory) : Tr.F("{0} : strobe désactivé (valeur {1} mémorisée).", _fixture.Name, current));
    }

    /// <summary>"Strobe 0" button: channel set to fixed (0, or open shutter); the box stays as it is.</summary>
    private void SetStrobeFixed()
    {
        if (_strobeIndex < 0)
        {
            return;
        }
        _ctx.Engine.SetValue(_fixture.Id, _strobeIndex, _profile.Channels[_strobeIndex].NoStrobeValue);
        _ctx.NotifyLiveStateChanged();
        _ctx.Status(Tr.F("{0} : strobe à 0.", _fixture.Name));
    }

    /// <summary>Strobe slider moved into a strobe value while the strobe is disabled: it is enabled again.</summary>
    private void ReactivateStrobeIfNeeded(int index, byte value)
    {
        if (index != _strobeIndex || _strobeOn is null || _strobeOn.Checked || !_profile.Channels[index].IsStrobeValue(value))
        {
            return;
        }
        _ctx.Engine.EditShow(_ => _fixture.NoStrobe = false);
        _ctx.MarkDirty();
        _updating = true;
        _strobeOn.Checked = true;
        _updating = false;
        _ctx.NotifyLiveStateChanged();
        _ctx.Status(Tr.F("{0} : strobe réactivé ({1}).", _fixture.Name, value));
    }

    /// <summary>Spider motor: 0..255 = 0..135° (manual).</summary>
    private static int Degrees(byte value) => (int)Math.Round(value * 135.0 / 255);

    /// <summary>Wings pad moved: X sets the motor positions (mirrored with "Invert"), Y sets the dimmer.</summary>
    private void OnWingsChanged(object? sender, EventArgs e)
    {
        if (_updating || _wings is null)
        {
            return;
        }

        byte position = (byte)Math.Round(_wings.ValueX / 65535.0 * 255);
        _ctx.Engine.SetMotors(_fixture.Id, position, _mirrorWings?.Checked == true);
        if (_dimmer >= 0)
        {
            _ctx.Engine.SetFixtureDimmer(_fixture.Id, (byte)Math.Round(255 - _wings.ValueY / 65535.0 * 255));
        }
        RefreshFromEngine();
    }

    /// <summary>
    /// "Invert" box: remembered in the patch (saved with the configuration). Wing 2 is realigned
    /// on wing 1 right away (mirrored or in the same direction).
    /// </summary>
    private void OnMirrorChanged()
    {
        if (_updating || _mirrorWings is null)
        {
            return;
        }

        bool mirror = _mirrorWings.Checked;
        _ctx.Engine.EditShow(_ => _fixture.MirrorMotors = mirror);
        _ctx.MarkDirty();

        byte[] values = _ctx.Engine.GetValues(_fixture.Id);
        if (_motors.Length >= 2 && _motors[0] < values.Length)
        {
            _ctx.Engine.SetMotors(_fixture.Id, values[_motors[0]], mirror);
        }
        RefreshFromEngine();
    }

    /// <summary>Left/right effect button: starts or stops the effect in the engine and saves the settings.</summary>
    private void OnSweepToggled()
    {
        if (_sweep is null)
        {
            return;
        }
        _ctx.Engine.SetWingSweep(_fixture.Id, _sweep.Checked);
        _sweep.BackColor = _sweep.Checked ? Color.FromArgb(255, 210, 60) : SystemColors.Control;
        _sweep.UseVisualStyleBackColor = !_sweep.Checked;
        _ctx.Status(_sweep.Checked
            ? Tr.F("{0} : effet gauche ↔ droite toutes les {1:0.#} s", _fixture.Name, _fixture.WingSweepDelaySeconds) +
              (_fixture.MirrorMotors ? Tr.T(" (ailes en miroir).") : ".")
            : Tr.F("{0} : effet gauche ↔ droite arrêté.", _fixture.Name));
    }

    /// <summary>"Mic" button: starts or stops the shared microphone.</summary>
    private void OnMicListenToggled()
    {
        if (_micUpdating || _micListen is null)
        {
            return;
        }
        if (!_micListen.Checked)
        {
            _ctx.Mic.Stop();
            return;
        }
        try
        {
            _ctx.Mic.Start();
            _ctx.Status(Tr.T("Micro : écoute en cours (le tempo apparaît après quelques secondes de musique)."));
        }
        catch (Exception ex)
        {
            _ctx.Mic.Stop();
            _ctx.Status(Tr.F("Impossible d'ouvrir le micro : {0}", ex.Message));
        }
    }

    /// <summary>"All": ticks or unticks every colour at once.</summary>
    private void OnMaskAllChanged()
    {
        if (_maskUpdating || _maskAll is null)
        {
            return;
        }
        _maskUpdating = true;
        foreach (var box in _maskBoxes)
        {
            box.Checked = _maskAll.Checked;
        }
        _maskUpdating = false;
        ApplyMask();
    }

    /// <summary>A colour mask box changed: updates the engine mask (see ApplyMask).</summary>
    private void OnMaskChanged()
    {
        if (_maskUpdating || _maskAll is null)
        {
            return;
        }
        _maskUpdating = true;
        _maskAll.Checked = _maskBoxes.All(b => b.Checked);
        _maskUpdating = false;
        ApplyMask();
    }

    /// <summary>
    /// Unticked colours -> engine mask: from the next frame they go out at 0. The slider values are
    /// kept: when ticked again, the colour comes back as before.
    /// </summary>
    private void ApplyMask()
    {
        var muted = _maskBoxes.Where(b => !b.Checked).Select(b => (ChannelType)b.Tag!).ToList();
        _ctx.Engine.EditShow(_ => _fixture.MutedColors = muted);
        _ctx.MarkDirty();
        _ctx.Status(muted.Count == 0
            ? Tr.F("{0} : toutes les couleurs actives.", _fixture.Name)
            : Tr.F("{0} : coupé en sortie → {1}.", _fixture.Name, (string.Join(", ", _maskBoxes.Where(b => !b.Checked).Select(b => b.Text)))));
    }

    /// <summary>Current mode, from the fixture (locked to the tempo or not) and the microphone (the BPM follows it or not).</summary>
    private SweepMode CurrentMode() =>
        !_fixture.WingSweepSyncToBpm ? SweepMode.Delay
        : _ctx.Mic.FollowBpm ? SweepMode.Mic
        : SweepMode.Bpm;

    /// <summary>A mode box was ticked: the others are unticked. Unticking the active box ticks it again.</summary>
    private void OnModeChecked(SweepMode mode, CheckBox box)
    {
        if (_micUpdating)
        {
            return;
        }
        if (!box.Checked)
        {
            ShowMode(CurrentMode()); // there must always be a mode
            return;
        }

        if (mode == SweepMode.Mic && !_ctx.Mic.IsListening && _micListen is not null)
        {
            _micListen.Checked = true; // -> OnMicListenToggled: switches the microphone on
        }
        _ctx.Mic.FollowBpm = mode == SweepMode.Mic;
        _ctx.Engine.EditShow(_ => _fixture.WingSweepSyncToBpm = mode != SweepMode.Delay);
        _ctx.MarkDirty();
        ShowMode(mode);
    }

    /// <summary>Ticks the mode box and enables only the useful settings.</summary>
    private void ShowMode(SweepMode mode)
    {
        if (_modeDelay is null || _sweepSync is null || _micFollow is null || _sweepDelay is null || _bpmBox is null || _sweepBeats is null)
        {
            return;
        }
        _micUpdating = true;
        try
        {
            _modeDelay.Checked = mode == SweepMode.Delay;
            _sweepSync.Checked = mode == SweepMode.Bpm;
            _micFollow.Checked = mode == SweepMode.Mic;
        }
        finally
        {
            _micUpdating = false;
        }
        _sweepDelay.Enabled = mode == SweepMode.Delay;
        _bpmBox.Enabled = mode == SweepMode.Bpm;
        _sweepBeats.Enabled = mode != SweepMode.Delay;
    }

    /// <summary>Typed BPM: it is the global BPM (Show tab), used by the effect in Tempo mode.</summary>
    private void OnBpmTyped()
    {
        if (_micUpdating || _bpmBox is null)
        {
            return;
        }
        _ctx.Engine.Bpm = (double)_bpmBox.Value;
        _ctx.Show.Settings.Bpm = _ctx.Engine.Bpm;
        _ctx.NotifyLiveStateChanged(); // the Show tab shows the new BPM
    }

    /// <summary>Microphone state -> controls of the tab (10 times/s while listening).</summary>
    private void UpdateMicUi()
    {
        if (IsDisposed || _micListen is null || _micBpm is null || _micFollow is null)
        {
            return;
        }
        var mic = _ctx.Mic;
        _micUpdating = true;
        try
        {
            _micListen.Checked = mic.IsListening;
            if (_bpmBox is not null && !_bpmBox.Focused)
            {
                Ui.SetValue(_bpmBox, _ctx.Engine.Bpm); // tap tempo or microphone
            }
        }
        finally
        {
            _micUpdating = false;
        }
        ShowMode(CurrentMode()); // the microphone may have been locked / unlocked from the Show tab
        _micListen.Text = mic.IsListening ? Tr.T("Micro : ON") : Tr.T("Micro");
        _micListen.BackColor = mic.IsListening ? Color.FromArgb(120, 200, 120) : SystemColors.Control;
        _micListen.UseVisualStyleBackColor = !mic.IsListening;
        double bpm = mic.Bpm;
        _micBpm.Text = !mic.IsListening ? Tr.F("BPM : {0:0.0}", _ctx.Engine.Bpm)
            : bpm > 0 ? Tr.F("Tempo micro : {0:0.0} BPM", bpm) : Tr.T("Tempo : écoute…");
        _micBpm.ForeColor = mic.IsListening && bpm > 0 ? Ui.OkColor : mic.IsListening ? Ui.WarningColor : Color.DimGray;
    }

    /// <summary>Delay and amplitude of the effect: saved with the configuration.</summary>
    private void SaveSweepSettings()
    {
        if (_sweepDelay is null || _sweepAmplitude is null || _sweepBeats is null)
        {
            return;
        }
        double delay = (double)_sweepDelay.Value;
        int amplitude = (int)_sweepAmplitude.Value;
        double beats = (double)_sweepBeats.Value;
        _ctx.Engine.EditShow(_ =>
        {
            _fixture.WingSweepDelaySeconds = delay;
            _fixture.WingSweepAmplitudePercent = amplitude;
            _fixture.WingSweepBeats = beats;
        });
        _ctx.MarkDirty();
    }

    /// <summary>XY pad moved: aims the moving head (16-bit pan/tilt).</summary>
    private void OnXyChanged(object? sender, EventArgs e)
    {
        if (_updating || _xy is null)
        {
            return;
        }
        _ctx.Engine.SetPanTilt16(_fixture.Id, _xy.ValueX, _xy.ValueY);
        RefreshFromEngine();
    }

    /// <summary>Preset picked in the list: moves the channel slider to its value.</summary>
    private void OnPresetChosen(int index)
    {
        if (_presets[index].SelectedItem is PresetItem preset)
        {
            _sliders[index].Value = preset.Value; // triggers OnSlider -> engine
        }
    }

    /// <summary>Selects in the list the preset matching the current value (or "— preset —").</summary>
    private void SyncPresetCombo(int index, byte value)
    {
        var combo = _presets[index];
        int match = 0;
        for (int k = 1; k < combo.Items.Count; k++)
        {
            if (combo.Items[k] is PresetItem p && p.Value == value)
            {
                match = k;
                break;
            }
        }
        if (combo.Items.Count > 0 && combo.SelectedIndex != match)
        {
            combo.SelectedIndex = match;
        }
    }

    /// <summary>"+" button: asks a name and saves the current value as a preset of the profile (shared by every fixture of the model).</summary>
    private void AddPreset(int index)
    {
        byte value = (byte)_sliders[index].Value;
        var channel = _profile.Channels[index];
        string? name = InputDialog.Ask(FindForm(), Tr.T("Nouveau preset"),
            Tr.F("Nom du preset pour « {0} » = {1}\n(ex. Rouge, Bleu, Gobo étoile, Ouvert...) :", Tr.T(channel.Name), value));
        if (name is null)
        {
            return;
        }

        // Profile change under the engine lock (the send thread reads the presets).
        _ctx.Engine.EditShow(_ =>
        {
            channel.Presets.RemoveAll(p => TextKey.Same(p.Name, name));
            channel.Presets.Add(new ChannelPreset(name, value));
            channel.Presets.Sort((a, b) => a.Value.CompareTo(b.Value));
        });
        _ctx.MarkDirty();
        _ctx.NotifyProfileDetailsChanged();
        _ctx.Status(Tr.F("Preset « {0} » = {1} ajouté au profil « {2} » (valable pour tous les appareils de ce modèle).", name, value, _profile.Name));
    }

    /// <summary>"OK" box of a channel: marks the channel as checked in the profile.</summary>
    private void OnVerifiedChanged(int index)
    {
        if (_updating)
        {
            return;
        }
        bool verified = _verified[index].Checked;
        _ctx.Engine.EditShow(_ => _profile.Channels[index].Verified = verified);
        _ctx.MarkDirty();
        _ctx.NotifyProfileDetailsChanged();
    }

    /// <summary>"Default values" button: every channel back to its profile default.</summary>
    private void ResetDefaults()
    {
        _ctx.Engine.ResetToDefaults(_fixture.Id);
        RefreshFromEngine();
    }

    /// <summary>Unsubscribes from the context and the microphone before the tab is destroyed.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Required: otherwise the context would keep a reference to this destroyed panel.
            _ctx.LiveStateChanged -= RefreshFromEngine;
            _ctx.ProfileDetailsChanged -= RefreshProfileDetails;
            _ctx.Mic.Changed -= UpdateMicUi;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
