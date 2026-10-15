// =====================================================================================
//  FixturePanel.cs
// -------------------------------------------------------------------------------------
//  Onglet d'UN appareil, genere automatiquement a partir de son profil :
//    - en-tete : nom, profil, plage d'adresses, avertissement si canaux non verifies
//    - pad XY pour orienter la lyre (si le profil a pan/tilt)
//    - pad "ailes" pour un appareil a 2 moteurs (Spider) : gauche/droite = position
//      des 2 ailes, haut/bas = dimmer ; case "Inverser" = ailes en miroir
//    - une ligne par canal : curseur 0-255, valeur, liste de presets,
//      bouton "+" (enregistrer la valeur actuelle comme preset), case "OK" (verifie)
//
//  Le bouton "+" est la facon la plus simple de completer un profil sur place :
//  on bouge le curseur de la roue de couleurs jusqu'au rouge, "+", on tape "Rouge"
//  -> le bouton Rouge de l'onglet Soiree fonctionne alors pour toutes les lyres
//  de ce modele.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Engine;
using DmxController.Core.Model;

namespace DmxController.UI;

[DesignerCategory("Code")]
internal sealed class FixturePanel : UserControl
{
    private sealed record PresetItem(string Name, byte Value)
    {
        public override string ToString() => $"{Name} ({Value})";
    }

    private const string NoPreset = "— preset —";

    private readonly ShowContext _ctx;
    private readonly FixtureInstance _fixture;
    private readonly FixtureProfile _profile;
    private readonly TrackBar[] _sliders;
    private readonly NumericUpDown[] _values; // valeur saisissable (et boutons + / -) a cote de chaque barre
    private readonly Label[] _names;
    private readonly ComboBox[] _presets;
    private readonly CheckBox[] _verified;
    private readonly XYPad? _xy;
    private readonly XYPad? _wings;
    private readonly CheckBox? _mirrorWings;
    private readonly Label? _wingsInfo;
    private readonly int[] _motors;   // index des canaux Moteur dans le profil
    private readonly int _dimmer;     // index du canal Dimmer (-1 si aucun)

    // Faders "LED gauche / droite" (appareils a ailes) : groupes de canaux de couleur, et
    // memoire des proportions de chaque groupe pour retrouver la couleur apres un passage a 0.
    private readonly VerticalFader? _ledLeft;
    private readonly VerticalFader? _ledRight;
    private readonly int[] _groupLeft = Array.Empty<int>();
    private readonly int[] _groupRight = Array.Empty<int>();
    private readonly double[]?[] _groupRatios = new double[]?[2];

    private readonly CheckBox? _strobeOn; // case « Strobe » (actif / desactive), si un canal sait strober
    private readonly int _strobeIndex = -1;

    // Effet gauche <-> droite des ailes (appareils a 2 moteurs)
    private readonly CheckBox? _sweep;
    private readonly NumericUpDown? _sweepDelay;
    private readonly NumericUpDown? _sweepAmplitude;
    private readonly CheckBox? _sweepSync;
    private readonly NumericUpDown? _sweepBeats;
    private readonly CheckBox? _micListen;   // ecouter le micro (partage avec l'onglet Soiree)
    private readonly Label? _micBpm;
    private readonly CheckBox? _micFollow;   // mode "tempo du micro"
    private readonly CheckBox? _modeDelay;   // mode "delai en secondes"
    private readonly NumericUpDown? _bpmBox; // mode "tempo en BPM" (= BPM de l'onglet Soiree)
    private bool _micUpdating;

    private enum SweepMode { Delay, Bpm, Mic }

    // Masque de couleurs (All, R, G, B, White) : cases cochees = couleurs autorisees en sortie.
    private readonly CheckBox? _maskAll;
    private readonly List<CheckBox> _maskBoxes = new();
    private bool _maskUpdating;
    private readonly Label _warning = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 6, 3, 3) };
    private readonly ToolTip _tips = new();
    private bool _updating;
    private bool _numberUpdating; // case numerique mise a jour par le code (pas par l'utilisateur)
    private readonly BeatEffectBlock? _beatBlock; // effet au tempo (projecteurs sans moteur)
    private readonly GroupLevelPad? _groupPad;    // graphique des groupes A, B, C, Tous

    // Bandeau affiche quand l'appareil joue un programme interne (canal Mode hors "Manuel").
    private readonly Label _modeLock = new()
    {
        Text = "Mode programme interne : dimmer, groupes, strobe et effet au tempo sont inactifs. " +
               "Choisis « Manuel (DMX direct) » dans le canal Mode pour les retrouver.",
        AutoSize = true,
        MaximumSize = new Size(1000, 0),
        Font = Ui.BoldFont,
        ForeColor = Ui.WarningColor,
        Visible = false,
        Margin = new Padding(3, 4, 3, 4),
    };
    private readonly VerticalFader? _groupsFader; // les 3 groupes ensemble
    private readonly int[] _groups = Array.Empty<int>(); // index des canaux de groupes de LED
    private double[]? _groupsRatios;

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

        // ---------------- En-tete ----------------
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
            Text = $"{fixture.Name}  —  {profile.Manufacturer} {profile.Name}".Trim(),
            AutoSize = true,
            Font = Ui.TitleFont,
        });
        header.Controls.Add(Ui.Text(
            $"Adresse DMX {fixture.Address} : canaux {fixture.Address} à {fixture.EndAddress(profile)}" +
            (fixture.Enabled ? "" : "   —   APPAREIL DÉSACTIVÉ dans le patch (rien n'est envoyé)"),
            color: fixture.Enabled ? Color.DimGray : Color.DarkRed));
        header.Controls.Add(_warning);
        if (!string.IsNullOrWhiteSpace(profile.Notes))
        {
            header.Controls.Add(new Label
            {
                Text = profile.Notes,
                AutoSize = true,
                MaximumSize = new Size(1000, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 2, 3, 6),
            });
        }

        // ---------------- Pad XY (lyres) ----------------
        Panel? left = null;
        if (profile.CanMove)
        {
            left = new Panel { Dock = DockStyle.Left, Width = 290, Padding = new Padding(10) };
            _xy = new XYPad { Location = new Point(12, 6), Size = new Size(260, 260) };
            _xy.ValueChanged += OnXyChanged;

            var center = new Button { Text = "Centrer", Location = new Point(12, 274), Width = 126, Height = 30 };
            center.Click += (_, _) =>
            {
                _ctx.Engine.SetPanTilt16(_fixture.Id, 32768, 32768);
                RefreshFromEngine();
            };
            var reset = new Button { Text = "Par défaut", Location = new Point(146, 274), Width = 126, Height = 30 };
            reset.Click += (_, _) => ResetDefaults();

            var hint = new Label
            {
                Text = "Clique / glisse dans le carré pour orienter la lyre.\n" +
                       "Les effets de mouvement de l'onglet Soirée tournent autour de cette position.",
                Location = new Point(12, 312),
                Size = new Size(260, 60),
                ForeColor = Color.DimGray,
            };
            left.Controls.AddRange(new Control[] { _xy, center, reset, hint });
        }
        else if (_motors.Length >= 2)
        {
            // Appareil a 2 moteurs (ailes du Spider) : X = position des ailes, Y = dimmer (haut = 100 %),
            // et a droite du pad, un fader vertical par aile pour ses LED.
            left = new Panel { Dock = DockStyle.Left, Width = 400, Padding = new Padding(10), AutoScroll = true };

            (_groupLeft, _groupRight) = LedGroups.Split(profile);
            if (_groupLeft.Length > 0 && _groupRight.Length > 0)
            {
                _ledLeft = new VerticalFader { Location = new Point(284, 6), Size = new Size(48, 200) };
                _ledRight = new VerticalFader { Location = new Point(340, 6), Size = new Size(48, 200) };
                _ledLeft.ValueChanged += (_, _) => OnLedFader(0);
                _ledRight.ValueChanged += (_, _) => OnLedFader(1);
                _tips.SetToolTip(_ledLeft, $"LED de l'aile gauche ({LedNames(_groupLeft)}) : bas = 0 %, haut = 100 %.\nGarde la couleur en cours (les proportions entre LED).");
                _tips.SetToolTip(_ledRight, $"LED de l'aile droite ({LedNames(_groupRight)}) : bas = 0 %, haut = 100 %.\nGarde la couleur en cours (les proportions entre LED).");
                left.Controls.Add(_ledLeft);
                left.Controls.Add(_ledRight);
                left.Controls.Add(new Label { Text = "LED\ngauche", Location = new Point(276, 210), Size = new Size(64, 46), TextAlign = ContentAlignment.TopCenter });
                left.Controls.Add(new Label { Text = "LED\ndroite", Location = new Point(332, 210), Size = new Size(64, 46), TextAlign = ContentAlignment.TopCenter });
            }
            _wings = new XYPad
            {
                Location = new Point(12, 6),
                Size = new Size(260, 200),
                XLabel = "Ailes ← gauche · droite →",
                YLabel = _dimmer >= 0 ? "Dimmer : haut = 100 %, bas = 0" : "",
            };
            _wings.ValueChanged += OnWingsChanged;

            _mirrorWings = new CheckBox
            {
                Text = "Inverser (ailes en miroir)",
                Checked = fixture.MirrorMotors,
                Location = new Point(12, 240),
                AutoSize = true,
            };
            _mirrorWings.CheckedChanged += (_, _) => OnMirrorChanged();

            var center = new Button { Text = "Ailes au milieu", Location = new Point(12, 266), Width = 126, Height = 30 };
            center.Click += (_, _) =>
            {
                _ctx.Engine.SetMotors(_fixture.Id, 128, _mirrorWings.Checked);
                RefreshFromEngine();
            };
            var reset = new Button { Text = "Par défaut", Location = new Point(146, 266), Width = 126, Height = 30 };
            reset.Click += (_, _) => ResetDefaults();

            _wingsInfo = new Label { Location = new Point(12, 302), Size = new Size(260, 20), Font = Ui.BoldFont };
            _tips.SetToolTip(_wings, "Gauche / droite : les 2 ailes (opposées avec « Inverser »). Haut / bas : dimmer.");
            left.Controls.AddRange(new Control[] { _wings, _mirrorWings, center, reset, _wingsInfo });

            // Masque de couleurs : une couleur decochee est forcee a 0 en sortie DMX, quoi qu'il arrive.
            var maskRow = new FlowLayoutPanel
            {
                Location = new Point(9, 210),
                Size = new Size(265, 26),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, // sinon il garde 100 px de haut et cache la suite
                WrapContents = false,
                Margin = new Padding(0),
            };
            _maskAll = new CheckBox { Text = "All", AutoSize = true, Font = Ui.BoldFont };
            maskRow.Controls.Add(_maskAll);
            var colors = new (ChannelType Type, string Label, Color Color)[]
            {
                (ChannelType.Red, "R", Color.Red), (ChannelType.Green, "G", Color.Green),
                (ChannelType.Blue, "B", Color.Blue), (ChannelType.White, "White", SystemColors.ControlText),
            };
            foreach (var (type, label, color) in colors)
            {
                if (!profile.Has(type))
                {
                    continue;
                }
                var box = new CheckBox { Text = label, AutoSize = true, Tag = type, ForeColor = color, Font = Ui.BoldFont, Checked = !fixture.MutedColors.Contains(type) };
                box.CheckedChanged += (_, _) => OnMaskChanged();
                _tips.SetToolTip(box, $"Décoché : toutes les LED {label} sont coupées (0) en sortie, même pendant un flash ou un chenillard.");
                _maskBoxes.Add(box);
                maskRow.Controls.Add(box);
            }
            _maskAll.Checked = _maskBoxes.All(b => b.Checked);
            _maskAll.CheckedChanged += (_, _) => OnMaskAllChanged();
            _tips.SetToolTip(_maskAll, "Coche / décoche toutes les couleurs.");
            left.Controls.Add(maskRow);

            // Effet automatique gauche <-> droite avec un delai (suit la case "Inverser").
            // Tableau (et non positions fixes) : les libelles prennent la place qu'il leur faut,
            // meme avec la mise a l'echelle de Windows (125 %, 150 %...).
            _sweep = new CheckBox
            {
                Text = "Effet gauche ↔ droite",
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = Ui.BoldFont,
                Size = new Size(260, 32),
                Checked = ctx.Engine.IsWingSweeping(fixture.Id),
            };
            _sweep.CheckedChanged += (_, _) => OnSweepToggled();
            _sweepDelay = new NumericUpDown { Minimum = 0.2m, Maximum = 60, DecimalPlaces = 1, Increment = 0.1m, Width = 70 };
            _sweepAmplitude = new NumericUpDown { Minimum = 5, Maximum = 100, Increment = 5, Width = 70 };
            // 3 modes qui s'excluent : delai fixe (s), tempo saisi en BPM, tempo detecte au micro.
            _modeDelay = new CheckBox { Text = "Délai", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            _sweepSync = new CheckBox { Text = "Tempo", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
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

            // Micro : meme micro que l'onglet Soiree (_ctx.Mic).
            _micListen = new CheckBox
            {
                Text = "Micro",
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(110, 28),
                Enabled = Audio.MicController.DeviceNames().Count > 0,
            };
            _micListen.CheckedChanged += (_, _) => OnMicListenToggled();
            _micBpm = new Label { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 8, 3, 3) };
            _micFollow = new CheckBox { Text = "Tempo du micro", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            _micFollow.CheckedChanged += (_, _) => OnModeChecked(SweepMode.Mic, _micFollow);

            _tips.SetToolTip(_sweep, "Les ailes vont à gauche, attendent le délai, vont à droite, attendent… " +
                                     "Avec « Inverser », l'aile 2 fait le mouvement opposé.");
            _tips.SetToolTip(_modeDelay, "Un aller toutes les N secondes.");
            _tips.SetToolTip(_sweepSync, "Tempo saisi en BPM (le même que l'onglet Soirée / tap tempo) : 1 aller = N temps.");
            _tips.SetToolTip(_micListen, "Écoute le micro pour détecter le tempo (le même micro que l'onglet Soirée).");
            _tips.SetToolTip(_micFollow, "Le BPM suit le tempo détecté au micro, et l'effet gauche/droite se cale dessus.");

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
            Row(_modeDelay, _sweepDelay, "s");
            Row(_sweepSync, _bpmBox, "BPM");
            Row(_micFollow, _micListen);
            Row(_micBpm);
            Row(Ui.Text("1 aller (tempo) ="), _sweepBeats, "temps");
            Row(Ui.Text("Amplitude"), _sweepAmplitude, "%");
            left.Controls.Add(effect);
            ShowMode(CurrentMode());

            _ctx.Mic.Changed += UpdateMicUi;
            UpdateMicUi();
        }
        else
        {
            var reset = new Button { Text = "Valeurs par défaut", AutoSize = true };
            reset.Click += (_, _) => ResetDefaults();
            header.Controls.Add(reset);
            header.Controls.Add(_modeLock);

            // Projecteur a plusieurs groupes de LED (PAR UV, PAR RGB...) : effet au tempo a gauche.
            if (profile.Channels.Count(c => ChannelTypeInfo.IsColorMix(c.Type)) >= 2)
            {
                left = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(10), AutoScroll = true };

                // Graphique des groupes (A, B, C, Tous) + fader des 3 groupes ensemble + TOUT COUPER.
                _groups = Enumerable.Range(0, count).Where(i => ChannelTypeInfo.IsColorMix(profile.Channels[i].Type)).ToArray();
                _groupPad = new GroupLevelPad
                {
                    Location = new Point(9, 6),
                    Size = new Size(240, 180),
                    GroupNames = _groups.Select((_, k) => ((char)('A' + k)).ToString()).ToArray(),
                };
                _groupPad.LevelChanged += OnGroupPad;
                _tips.SetToolTip(_groupPad, "X : groupe A, B, C ou Tous. Y : niveau (bas = 0 %, haut = 100 %). Clique ou glisse.");
                _groupsFader = new VerticalFader { Location = new Point(258, 6), Size = new Size(48, 180), FillColor = Color.FromArgb(160, 100, 240) };
                _groupsFader.ValueChanged += (_, _) => OnGroupsFader();
                _tips.SetToolTip(_groupsFader, "Les 3 groupes en même temps (garde leurs proportions).");
                var faderLabel = new Label { Text = "3 groupes", Location = new Point(250, 188), Size = new Size(64, 18), TextAlign = ContentAlignment.TopCenter };
                var cutAll = new Button
                {
                    Text = "TOUT COUPER",
                    Location = new Point(9, 210),
                    Size = new Size(297, 34),
                    Font = Ui.BoldFont,
                    BackColor = Color.FromArgb(190, 40, 40),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                };
                cutAll.Click += (_, _) => CutAll();
                _tips.SetToolTip(cutAll, "Dimmer, groupes et strobe à 0, effet au tempo arrêté.");

                _beatBlock = new BeatEffectBlock(ctx, fixture, profile) { Location = new Point(9, 252) };
                left.Controls.AddRange(new Control[] { _groupPad, _groupsFader, faderLabel, cutAll, _beatBlock });
            }
        }

        // ---------------- Une ligne par canal ----------------
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
            int index = i; // capture pour les lambdas
            var channel = profile.Channels[i];

            _names[i] = new Label { AutoSize = true, MinimumSize = new Size(260, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            _tips.SetToolTip(_names[i], $"{ChannelTypeInfo.DisplayName(channel.Type)}" +
                                        (string.IsNullOrWhiteSpace(channel.Notes) ? "" : $"\n{channel.Notes}"));

            _sliders[i] = Ui.Slider(0, 255, left is null ? 330 : 230); // plus etroit si un pad occupe la gauche
            _sliders[i].ValueChanged += (_, _) => OnSlider(index);

            _values[i] = new NumericUpDown { Minimum = 0, Maximum = 255, Width = 58, Margin = new Padding(3, 4, 3, 3) }; _values[i].ValueChanged += (_, _) => OnNumberTyped(index);

            _presets[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, DropDownWidth = 260, Margin = new Padding(3, 4, 3, 3) };
            _presets[i].SelectionChangeCommitted += (_, _) => OnPresetChosen(index);

            var add = new Button { Text = "+", Width = 28, Height = 26, Margin = new Padding(3, 3, 3, 3) };
            _tips.SetToolTip(add, "Enregistrer la valeur actuelle comme preset (ex. « Rouge » sur la roue de couleurs)");
            add.Click += (_, _) => AddPreset(index);

            _verified[i] = new CheckBox { Text = "OK", AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
            _tips.SetToolTip(_verified[i], "Coche quand tu as vérifié sur l'appareil que ce canal fait bien ce qui est indiqué");
            _verified[i].CheckedChanged += (_, _) => OnVerifiedChanged(index);

            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(_names[i], 0, i);
            table.Controls.Add(_sliders[i], 1, i);
            table.Controls.Add(_values[i], 2, i);
            table.Controls.Add(_presets[i], 3, i);
            table.Controls.Add(add, 4, i);
            table.Controls.Add(_verified[i], 5, i);

            // A cote du (premier) canal qui sait strober (canal Strobe, obturateur de la 260X...) :
            // case « Strobe » (actif / desactive avec memoire de la valeur) + bouton « Strobe 0 ».
            if (channel.IsStrobeCapable && _strobeOn is null)
            {
                _strobeIndex = i;
                _strobeOn = new CheckBox
                {
                    Text = "Strobe",
                    AutoSize = true,
                    Checked = !fixture.NoStrobe,
                    Font = Ui.BoldFont,
                    Margin = new Padding(10, 6, 3, 3),
                };
                _strobeOn.CheckedChanged += (_, _) => OnStrobeToggled();
                var strobeZero = new Button { Text = "Strobe 0", AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
                strobeZero.Click += (_, _) => SetStrobeFixed();
                _tips.SetToolTip(_strobeOn, "Décoché : strobe désactivé (canal fixe, la valeur est mémorisée ; rien ne peut le faire strober). " +
                                            "Recoché : l'ancienne valeur revient. Bouger la barre dans une valeur de strobe le réactive.");
                _tips.SetToolTip(strobeZero, channel.Type == ChannelType.Strobe
                    ? "Met le strobe à 0 (pas de strobe)."
                    : $"Remet l'obturateur en fixe (ouvert, {channel.NoStrobeValue}) : plus de strobe.");
                table.Controls.Add(Ui.Flow(0, _strobeOn, strobeZero), 6, i);
            }
        }
        scroll.Controls.Add(table);

        // Ordre d'ajout : la zone "Fill" en premier, les bords ensuite.
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

    public string FixtureId => _fixture.Id;

    /// <summary>Recopie les valeurs du moteur dans les curseurs (apres un rappel de scene, une couleur...).</summary>
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
        UpdateMicUi(); // BPM de l'effet (tap tempo de l'onglet Soiree...)
        _beatBlock?.UpdateUi();
    }

    /// <summary>Met a jour presets, cases "OK", libelles "(?)" et avertissement.</summary>
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
                _names[i].Text = $"{i + 1}. {channel.Name}   [DMX {_fixture.Address + i}]" + (channel.Verified ? "" : "  (?)");
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
                ? "Profil entièrement vérifié."
                : $"Profil à vérifier : {unverified} canal(aux) non confirmé(s), marqués (?). " +
                  "Teste-les (curseur ou onglet Debug) puis coche « OK ».";
            _warning.ForeColor = unverified == 0 ? Ui.OkColor : Ui.WarningColor;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Graphique : colonne d'un groupe -> ce groupe ; derniere colonne -> les 3 au meme niveau.</summary>
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

    /// <summary>Fader "3 groupes" : niveau commun en gardant les proportions entre groupes.</summary>
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

    /// <summary>Le dimmer general doit etre ouvert, sinon regler un groupe ne se verrait pas.</summary>
    private void OpenDimmerIfClosed()
    {
        byte[] values = _ctx.Engine.GetValues(_fixture.Id);
        if (_dimmer >= 0 && _dimmer < values.Length && values[_dimmer] == 0)
        {
            _ctx.Engine.SetFixtureDimmer(_fixture.Id, 255);
        }
    }

    /// <summary>
    /// Mode de l'appareil (canal Mode) : hors « Manuel », il joue ses programmes internes et ignore
    /// dimmer, groupes et strobe -> ces reglages, le graphique et l'effet au tempo sont grises
    /// (et l'effet arrete). Le canal Mode (avec ses presets) et la vitesse restent utilisables.
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
            strobeRow.Enabled = manual; // case « Strobe » + bouton « Strobe 0 »
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
                _beatBlock.Stop(); // un programme interne remplace l'effet
            }
            _beatBlock.Enabled = manual;
        }
        _modeLock.Visible = !manual;
    }

    /// <summary>TOUT COUPER : effet au tempo arrete, mode manuel, dimmer, groupes et strobe a 0.</summary>
    private void CutAll()
    {
        _beatBlock?.Stop();
        _ctx.Engine.SetFixtureStrobe(_fixture.Id, false);
        if (_profile.ManualModeRange() is { } mode)
        {
            _ctx.Engine.SetValue(_fixture.Id, mode.Index, mode.Manual.From); // arrete les programmes internes
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
        _ctx.Status($"{_fixture.Name} : tout coupé.");
    }

    /// <summary>Graphique et fader des groupes recales sur les valeurs actuelles.</summary>
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
            _groupsRatios = LedGroups.Ratios(values, _groups); // proportions memorisees (voir RememberRatios)
        }
    }

    /// <summary>Affiche une valeur dans la case numerique sans relancer d'evenement.</summary>
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

    /// <summary>Valeur tapee (ou + / -) : la barre suit, et c'est elle qui envoie au moteur.</summary>
    private void OnNumberTyped(int index)
    {
        if (_numberUpdating || _updating)
        {
            return;
        }
        _sliders[index].Value = (int)_values[index].Value; // -> OnSlider
    }

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
            SyncPads(_ctx.Engine.GetValues(_fixture.Id)); // Macro : mode manuel / programme -> grise ou non
        }
    }

    /// <summary>Recale les pads (lyre et ailes) sur les valeurs actuelles des canaux.</summary>
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
                _wings.ValueY = (255 - values[_dimmer]) * 257; // haut du pad = dimmer a fond
            }
            _wingsInfo.Text = $"Aile 1 : {Degrees(wing1)}°   Aile 2 : {Degrees(wing2)}°" +
                              (_dimmer >= 0 && _dimmer < values.Length ? $"   Dimmer : {values[_dimmer] * 100 / 255} %" : "");
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
    /// Memorise les proportions d'un groupe de LED tant qu'elles sont assez lumineuses pour etre
    /// precises : ainsi, une aile rouge descendue a 0 puis remontee redevient rouge.
    /// </summary>
    private void RememberRatios(int side, byte[] values, int[] group)
    {
        if (LedGroups.Level(values, group) >= 32)
        {
            _groupRatios[side] = LedGroups.Ratios(values, group);
        }
    }

    private void OnLedFader(int side)
    {
        if (_updating)
        {
            return;
        }

        var fader = side == 0 ? _ledLeft! : _ledRight!;
        var group = side == 0 ? _groupLeft : _groupRight;
        _ctx.Engine.SetGroupLevel(_fixture.Id, group, (byte)fader.Value, _groupRatios[side]);

        // Mise a jour des curseurs des LED sans relire la position du fader en cours de glisse.
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

    private string LedNames(int[] group) => string.Join(", ", group.Select(i => _profile.Channels[i].Name));

    /// <summary>
    /// Case « Strobe » : decochee -> la valeur actuelle est memorisee, le canal repasse en fixe et le
    /// moteur bloque tout strobe (FixtureInstance.NoStrobe) ; cochee -> l'ancienne valeur revient.
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
                _fixture.StrobeMemory = current; // pour le retrouver en recochant
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
        _ctx.NotifyLiveStateChanged(); // ce panneau + l'onglet Soiree (interrupteur STROBE)
        _ctx.Status(on ? $"{_fixture.Name} : strobe réactivé ({_fixture.StrobeMemory})." : $"{_fixture.Name} : strobe désactivé (valeur {current} mémorisée).");
    }

    /// <summary>Bouton « Strobe 0 » : canal en fixe (0, ou obturateur ouvert), la case reste comme elle est.</summary>
    private void SetStrobeFixed()
    {
        if (_strobeIndex < 0)
        {
            return;
        }
        _ctx.Engine.SetValue(_fixture.Id, _strobeIndex, _profile.Channels[_strobeIndex].NoStrobeValue);
        _ctx.NotifyLiveStateChanged();
        _ctx.Status($"{_fixture.Name} : strobe à 0.");
    }

    /// <summary>Barre du strobe bougee dans une valeur de strobe alors qu'il est desactive : on le reactive.</summary>
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
        _ctx.Status($"{_fixture.Name} : strobe réactivé ({value}).");
    }

    /// <summary>Moteur du Spider : 0..255 = 0..135° (notice).</summary>
    private static int Degrees(byte value) => (int)Math.Round(value * 135.0 / 255);

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
    /// Case "Inverser" : memorisee dans le patch (sauvegardee avec la config). On realigne
    /// tout de suite l'aile 2 sur l'aile 1 (en miroir ou dans le meme sens).
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
            ? $"{_fixture.Name} : effet gauche ↔ droite toutes les {_fixture.WingSweepDelaySeconds:0.#} s" +
              (_fixture.MirrorMotors ? " (ailes en miroir)." : ".")
            : $"{_fixture.Name} : effet gauche ↔ droite arrêté.");
    }

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
            _ctx.Status("Micro : écoute en cours (le tempo apparaît après quelques secondes de musique).");
        }
        catch (Exception ex)
        {
            _ctx.Mic.Stop();
            _ctx.Status($"Impossible d'ouvrir le micro : {ex.Message}");
        }
    }

    /// <summary>"All" : coche ou decoche toutes les couleurs d'un coup.</summary>
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
    /// Couleurs decochees -> masque du moteur : des la trame suivante elles sortent a 0. Les valeurs
    /// des curseurs sont gardees : en recochant, la couleur revient comme avant.
    /// </summary>
    private void ApplyMask()
    {
        var muted = _maskBoxes.Where(b => !b.Checked).Select(b => (ChannelType)b.Tag!).ToList();
        _ctx.Engine.EditShow(_ => _fixture.MutedColors = muted);
        _ctx.MarkDirty();
        _ctx.Status(muted.Count == 0
            ? $"{_fixture.Name} : toutes les couleurs actives."
            : $"{_fixture.Name} : coupé en sortie → {string.Join(", ", _maskBoxes.Where(b => !b.Checked).Select(b => b.Text))}.");
    }

    /// <summary>Mode actuel, d'apres l'appareil (cale ou non sur le tempo) et le micro (le BPM le suit ou non).</summary>
    private SweepMode CurrentMode() =>
        !_fixture.WingSweepSyncToBpm ? SweepMode.Delay
        : _ctx.Mic.FollowBpm ? SweepMode.Mic
        : SweepMode.Bpm;

    /// <summary>Une case de mode cochee : les autres se decochent. Decocher la case active la recoche.</summary>
    private void OnModeChecked(SweepMode mode, CheckBox box)
    {
        if (_micUpdating)
        {
            return;
        }
        if (!box.Checked)
        {
            ShowMode(CurrentMode()); // il faut toujours un mode
            return;
        }

        if (mode == SweepMode.Mic && !_ctx.Mic.IsListening && _micListen is not null)
        {
            _micListen.Checked = true; // -> OnMicListenToggled : allume le micro
        }
        _ctx.Mic.FollowBpm = mode == SweepMode.Mic;
        _ctx.Engine.EditShow(_ => _fixture.WingSweepSyncToBpm = mode != SweepMode.Delay);
        _ctx.MarkDirty();
        ShowMode(mode);
    }

    /// <summary>Coche la case du mode et n'active que les reglages utiles.</summary>
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

    /// <summary>BPM saisi : c'est le BPM general (onglet Soiree), utilise par l'effet en mode Tempo.</summary>
    private void OnBpmTyped()
    {
        if (_micUpdating || _bpmBox is null)
        {
            return;
        }
        _ctx.Engine.Bpm = (double)_bpmBox.Value;
        _ctx.Show.Settings.Bpm = _ctx.Engine.Bpm;
        _ctx.NotifyLiveStateChanged(); // l'onglet Soiree affiche le nouveau BPM
    }

    /// <summary>Etat du micro -> controles de l'onglet (10 fois/s pendant l'ecoute).</summary>
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
                Ui.SetValue(_bpmBox, _ctx.Engine.Bpm); // tap tempo ou micro
            }
        }
        finally
        {
            _micUpdating = false;
        }
        ShowMode(CurrentMode()); // le micro a pu etre cale / decale depuis l'onglet Soiree
        _micListen.Text = mic.IsListening ? "Micro : ON" : "Micro";
        _micListen.BackColor = mic.IsListening ? Color.FromArgb(120, 200, 120) : SystemColors.Control;
        _micListen.UseVisualStyleBackColor = !mic.IsListening;
        double bpm = mic.Bpm;
        _micBpm.Text = !mic.IsListening ? $"BPM : {_ctx.Engine.Bpm:0.0}"
            : bpm > 0 ? $"Tempo micro : {bpm:0.0} BPM" : "Tempo : écoute…";
        _micBpm.ForeColor = mic.IsListening && bpm > 0 ? Ui.OkColor : mic.IsListening ? Ui.WarningColor : Color.DimGray;
    }

    /// <summary>Delai et amplitude de l'effet : enregistres avec la configuration.</summary>
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

    private void OnXyChanged(object? sender, EventArgs e)
    {
        if (_updating || _xy is null)
        {
            return;
        }
        _ctx.Engine.SetPanTilt16(_fixture.Id, _xy.ValueX, _xy.ValueY);
        RefreshFromEngine();
    }

    private void OnPresetChosen(int index)
    {
        if (_presets[index].SelectedItem is PresetItem preset)
        {
            _sliders[index].Value = preset.Value; // declenche OnSlider -> moteur
        }
    }

    /// <summary>Selectionne dans la liste le preset correspondant a la valeur actuelle (ou "— preset —").</summary>
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

    private void AddPreset(int index)
    {
        byte value = (byte)_sliders[index].Value;
        var channel = _profile.Channels[index];
        string? name = InputDialog.Ask(FindForm(), "Nouveau preset",
            $"Nom du preset pour « {channel.Name} » = {value}\n(ex. Rouge, Bleu, Gobo étoile, Ouvert...) :");
        if (name is null)
        {
            return;
        }

        // Modification du profil sous le verrou du moteur (le thread d'envoi lit les presets).
        _ctx.Engine.EditShow(_ =>
        {
            channel.Presets.RemoveAll(p => TextKey.Same(p.Name, name));
            channel.Presets.Add(new ChannelPreset(name, value));
            channel.Presets.Sort((a, b) => a.Value.CompareTo(b.Value));
        });
        _ctx.MarkDirty();
        _ctx.NotifyProfileDetailsChanged();
        _ctx.Status($"Preset « {name} » = {value} ajouté au profil « {_profile.Name} » (valable pour tous les appareils de ce modèle).");
    }

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

    private void ResetDefaults()
    {
        _ctx.Engine.ResetToDefaults(_fixture.Id);
        RefreshFromEngine();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Indispensable : sinon le contexte garderait une reference vers ce panneau detruit.
            _ctx.LiveStateChanged -= RefreshFromEngine;
            _ctx.ProfileDetailsChanged -= RefreshProfileDetails;
            _ctx.Mic.Changed -= UpdateMicUi;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
