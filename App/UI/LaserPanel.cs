// =====================================================================================
//  LaserPanel.cs
// -------------------------------------------------------------------------------------
//  Onglet special LASER (ex. Showtec Galactic RGY-140 MKII, 10 canaux) :
//
//    - ALLUMER / ETEINDRE le faisceau (couleur 0 = laser eteint) + avertissement ;
//    - mode : programmes auto / au son internes, ou DMX (manuel) ;
//    - 32 boutons de motifs ; boutons de couleurs ;
//    - pad X/Y pour la position du motif ;
//    - pour les autres canaux (strobe, zoom, X, Y, rotations) : une liste deroulante
//      des plages de la notice (« Position fixe », « Rotation horaire »...) et un
//      curseur dans la plage choisie (de lent a rapide, d'un angle a l'autre...).
//
//  Securite : au BLACKOUT de l'onglet Soiree (ou master a 0), le moteur force le
//  laser en mode DMX et couleur 0 (eteint).
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;

namespace DmxController.UI;

/// <summary>Liste des plages d'un canal + curseur dans la plage choisie.</summary>
[DesignerCategory("Code")]
internal sealed class RangeControl : FlowLayoutPanel
{
    private readonly ChannelDef _channel;
    private readonly ComboBox _ranges = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(3, 4, 3, 3) };
    private readonly TrackBar _slider = Ui.Slider(0, 100, 200);
    private readonly Label _value = Ui.ValueLabel(40);
    private bool _updating;

    /// <summary>Leve quand l'utilisateur change la valeur.</summary>
    public event Action<byte>? ValueChanged;

    public RangeControl(ChannelDef channel)
    {
        _channel = channel;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        Margin = new Padding(0);

        foreach (var range in channel.Ranges)
        {
            _ranges.Items.Add(range);
        }
        Controls.AddRange(new Control[] { _ranges, _slider, _value });

        _ranges.SelectionChangeCommitted += (_, _) => Emit();
        _slider.ValueChanged += (_, _) =>
        {
            if (!_updating)
            {
                Emit();
            }
        };
    }

    private ChannelRange? Current => _ranges.SelectedItem as ChannelRange;

    private void Emit()
    {
        if (Current is not ChannelRange range)
        {
            return;
        }
        _slider.Enabled = range.From != range.To;
        byte value = range.At(_slider.Value / 100.0);
        _value.Text = value.ToString();
        ValueChanged?.Invoke(value);
    }

    /// <summary>Affiche une valeur venant du moteur (sans lever ValueChanged).</summary>
    public void Show(byte value)
    {
        _updating = true;
        try
        {
            var range = _channel.Ranges.FirstOrDefault(r => r.Contains(value));
            if (range is not null)
            {
                _ranges.SelectedItem = range;
                _slider.Value = (int)Math.Round(range.FractionOf(value) * 100);
                _slider.Enabled = range.From != range.To;
            }
            _value.Text = value.ToString();
        }
        finally
        {
            _updating = false;
        }
    }
}

/// <summary>Onglet d'un laser.</summary>
[DesignerCategory("Code")]
internal sealed class LaserPanel : UserControl
{
    private readonly ShowContext _ctx;
    private readonly FixtureInstance _fixture;
    private readonly FixtureProfile _profile;

    private readonly int _mode;     // index des canaux dans le profil (-1 si absent)
    private readonly int _pattern;
    private readonly int _color;
    private readonly int _x;
    private readonly int _y;

    private readonly Button _onOff = new() { Size = new Size(260, 56), Font = Ui.BigFont, Margin = new Padding(6) };
    private readonly Label _state = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(6, 8, 3, 3) };
    private readonly List<Button> _modeButtons = new();
    private readonly List<Button> _patternButtons = new();
    private readonly List<Button> _colorButtons = new();
    private readonly XYPad _pad = new() { Size = new Size(240, 240), XLabel = "X →", YLabel = "Y ↓" };
    private readonly Dictionary<int, RangeControl> _rangeControls = new();
    private byte _lastColor = 37; // couleur rendue a l'allumage ("couleurs d'origine")
    private bool _updating;

    public LaserPanel(ShowContext ctx, FixtureInstance fixture, FixtureProfile profile)
    {
        _ctx = ctx;
        _fixture = fixture;
        _profile = profile;
        Dock = DockStyle.Fill;

        _mode = profile.IndexOf(ChannelType.LaserMode);
        _pattern = profile.IndexOf(ChannelType.LaserPattern);
        _color = profile.IndexOf(ChannelType.LaserColor);
        _x = profile.IndexOf(ChannelType.LaserX);
        _y = profile.IndexOf(ChannelType.LaserY);

        // Page = une colonne de sections (TableLayoutPanel) dans une zone qui defile : plus fiable
        // qu'un FlowLayoutPanel avec des sauts de ligne autour de GroupBox a taille automatique.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var page = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Location = new Point(0, 0),
            Padding = new Padding(8),
        };
        scroll.Controls.Add(page);

        // ---------------- En-tete + securite ----------------
        var header = Ui.Table(1);
        header.Controls.Add(new Label { Text = $"{fixture.Name}  —  {profile.Manufacturer} {profile.Name}".Trim(), AutoSize = true, Font = Ui.TitleFont });
        header.Controls.Add(Ui.Text($"Adresse DMX {fixture.Address} : canaux {fixture.Address} à {fixture.EndAddress(profile)}", color: Color.DimGray));
        header.Controls.Add(new Label
        {
            Text = "⚠ Laser : ne jamais viser le public à hauteur des yeux. Le BLACKOUT (onglet Soirée, Espace) éteint aussi le laser.",
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = Color.DarkRed,
            Font = Ui.BoldFont,
            Margin = new Padding(3, 4, 3, 6),
        });
        _onOff.Click += (_, _) => ToggleBeam();
        header.Controls.Add(Ui.Flow(0, _onOff, _state));
        page.Controls.Add(header);

        // ---------------- Mode ----------------
        if (_mode >= 0)
        {
            var modes = Ui.Flow(0); // une seule ligne (avec retour a la ligne, la hauteur calculee serait fausse)
            foreach (var preset in profile.Channels[_mode].Presets)
            {
                var button = new Button { Text = preset.Name, AutoSize = true, Tag = preset.Value, Margin = new Padding(3) };
                if (preset.Name.Contains('⚠'))
                {
                    // Bouton a risque (ex. « Son rouge » qui met ce laser en mode test) : confirmation.
                    button.ForeColor = Ui.WarningColor;
                    button.Font = Ui.BoldFont;
                    button.Click += (_, _) => ConfirmRiskyMode(preset);
                }
                else
                {
                    button.Click += (_, _) => SetChannel(_mode, preset.Value);
                }
                _modeButtons.Add(button);
                modes.Controls.Add(button);
            }
            var group = Ui.Group("Mode (programmes internes ou DMX manuel)", modes);
            page.Controls.Add(group);
        }

        // ---------------- Motifs | couleurs + position (cote a cote) ----------------
        var middle = Ui.Flow(0);
        if (_pattern >= 0)
        {
            var grid = Ui.Flow(8 * 52);
            var ranges = profile.Channels[_pattern].Ranges;
            for (int n = 0; n < ranges.Count; n++)
            {
                var range = ranges[n];
                var button = new Button { Text = (n + 1).ToString(), Size = new Size(46, 34), Tag = range, Margin = new Padding(3) };
                button.Click += (_, _) =>
                {
                    EnsureDmxMode();
                    SetChannel(_pattern, range.At(0.5));
                };
                _patternButtons.Add(button);
                grid.Controls.Add(button);
            }
            middle.Controls.Add(Ui.Group("Motif (32 motifs)", grid));
        }

        // ---------------- Couleurs + pad ----------------
        var right = Ui.Table(1);
        if (_color >= 0)
        {
            var colors = Ui.Flow(420);
            foreach (var preset in profile.Channels[_color].Presets)
            {
                var button = new Button { Text = preset.Name, Size = new Size(130, 36), Tag = preset.Value, Margin = new Padding(3), FlatStyle = FlatStyle.Flat };
                button.BackColor = LaserColorFor(preset.Name);
                button.ForeColor = button.BackColor.GetBrightness() < 0.45f ? Color.White : Color.Black;
                button.Click += (_, _) =>
                {
                    EnsureDmxMode();
                    SetChannel(_color, preset.Value);
                };
                _colorButtons.Add(button);
                colors.Controls.Add(button);
            }
            right.Controls.Add(Ui.Group("Couleur", colors));
        }
        if (_x >= 0 && _y >= 0)
        {
            _pad.ValueChanged += (_, _) => OnPad();
            var center = new Button { Text = "Centrer", AutoSize = true };
            center.Click += (_, _) =>
            {
                SetChannel(_x, 64);
                SetChannel(_y, 64);
            };
            // Laser retourne / accroche a l'envers : le moteur inverse X et / ou Y en sortie DMX.
            var invertX = new CheckBox { Text = "Inverser X (gauche ↔ droite)", AutoSize = true, Checked = fixture.InvertPan };
            var invertY = new CheckBox { Text = "Inverser Y (haut ↔ bas)", AutoSize = true, Checked = fixture.InvertTilt };
            invertX.CheckedChanged += (_, _) => SetInvert(x: invertX.Checked, y: invertY.Checked);
            invertY.CheckedChanged += (_, _) => SetInvert(x: invertX.Checked, y: invertY.Checked);

            var padBox = Ui.Table(1);
            padBox.Controls.Add(_pad);
            padBox.Controls.Add(Ui.Flow(0, center, Ui.Text("Position fixe du motif (X et Y)", color: Color.DimGray)));
            padBox.Controls.Add(Ui.Flow(0, invertX, invertY));
            padBox.Controls.Add(Ui.Text("(laser retourné : inverse les positions et le sens des mouvements)", color: Color.DimGray));
            right.Controls.Add(Ui.Group("Position", padBox));
        }
        right.Dock = DockStyle.None;
        middle.Controls.Add(right);
        page.Controls.Add(middle);

        // ---------------- Autres canaux : plage + curseur ----------------
        var others = Ui.Table(2);
        for (int i = 0; i < profile.ChannelCount; i++)
        {
            var channel = profile.Channels[i];
            if (i == _mode || i == _pattern || i == _color || channel.Ranges.Count == 0)
            {
                continue;
            }
            int index = i;
            var control = new RangeControl(channel);
            control.ValueChanged += value => SetChannel(index, value);
            _rangeControls[i] = control;
            Ui.Row(others, $"{channel.Name}  [DMX {fixture.Address + i}]", control);
        }
        var reset = new Button { Text = "Tout remettre par défaut (laser éteint, mode DMX)", AutoSize = true };
        reset.Click += (_, _) =>
        {
            _ctx.Engine.ResetToDefaults(_fixture.Id);
            _ctx.NotifyLiveStateChanged();
        };
        others.Controls.Add(reset);
        page.Controls.Add(Ui.Group("Effets (choisis la plage, puis règle dans la plage)", others));

        if (!string.IsNullOrWhiteSpace(profile.Notes))
        {
            var notes = new Label { Text = profile.Notes, AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Color.DimGray, Margin = new Padding(6) };
            page.Controls.Add(notes);
        }

        Controls.Add(scroll);

        _ctx.LiveStateChanged += RefreshFromEngine;
        RefreshFromEngine();
    }

    /// <summary>Couleur d'affichage d'un bouton de couleur laser, d'apres son nom.</summary>
    private static Color LaserColorFor(string name)
    {
        string key = TextKey.Normalize(name);
        if (key.StartsWith("eteint"))
        {
            return Color.FromArgb(40, 40, 40);
        }
        bool red = key.Contains("rouge");
        bool green = key.Contains("vert");
        bool yellow = key.Contains("jaune");
        return (red, green, yellow) switch
        {
            (true, false, false) => Color.FromArgb(230, 40, 40),
            (false, true, false) => Color.FromArgb(40, 200, 60),
            (false, false, true) => Color.FromArgb(240, 210, 30),
            (true, true, false) => Color.FromArgb(200, 160, 40),
            (false, true, true) => Color.FromArgb(150, 220, 40),
            (true, false, true) => Color.FromArgb(240, 140, 30),
            (true, true, true) => Color.FromArgb(210, 170, 60),
            _ => Color.FromArgb(180, 140, 220),
        };
    }

    /// <summary>Cases « Inverser X / Y » : enregistrees avec l'appareil (InvertPan / InvertTilt).</summary>
    private void SetInvert(bool x, bool y)
    {
        _ctx.Engine.EditShow(_ =>
        {
            _fixture.InvertPan = x;
            _fixture.InvertTilt = y;
        });
        _ctx.MarkDirty();
        _ctx.Status($"{_fixture.Name} : X {(x ? "inversé" : "normal")}, Y {(y ? "inversé" : "normal")}.");
    }

    /// <summary>
    /// Mode a risque (⚠) : sur ce laser, « Son rouge » le met en mode test (« tst ») ; il n'ecoute
    /// alors plus le DMX et rien ne permet d'en sortir a distance. Confirmation obligatoire.
    /// </summary>
    private void ConfirmRiskyMode(ChannelPreset preset)
    {
        bool ok = Ui.Confirm(FindForm()!,
            $"« {preset.Name} » : sur ton laser, ce programme le fait passer en mode TEST (« tst » sur l'écran).\n\n" +
            "En mode test, le laser N'ÉCOUTE PLUS LE DMX (son adressage est coupé) : plus aucun bouton de ce logiciel ne " +
            "le pilote, et le manuel ne prévoit ni délai ni commande DMX pour en sortir.\n\n" +
            "Pour en sortir, SUR LE LASER : bouton FUNC jusqu'à l'adresse DMX (« d… »), puis ENTER — ou l'éteindre et le rallumer.\n\n" +
            "Envoyer quand même ?",
            "Mode test du laser");
        if (ok)
        {
            SetChannel(_mode, preset.Value);
            _ctx.Status($"{_fixture.Name} : « {preset.Name} » envoyé — pour sortir du mode test : FUNC → adresse DMX → ENTER sur le laser.");
        }
    }

    private void SetChannel(int index, byte value)
    {
        if (index < 0)
        {
            return;
        }
        _ctx.Engine.SetValue(_fixture.Id, index, value);
        RefreshFromEngine();
    }

    /// <summary>Motifs, couleurs et mouvements ne marchent qu'en mode DMX (canal 1 = 240-255).</summary>
    private void EnsureDmxMode()
    {
        if (_mode < 0)
        {
            return;
        }
        var dmx = _profile.Channels[_mode].Ranges.LastOrDefault(r => TextKey.Normalize(r.Name).StartsWith("dmx"));
        byte current = _ctx.Engine.GetValues(_fixture.Id).ElementAtOrDefault(_mode);
        if (dmx is not null && !dmx.Contains(current))
        {
            _ctx.Engine.SetValue(_fixture.Id, _mode, dmx.At(0.5));
        }
    }

    private void ToggleBeam()
    {
        if (_color < 0)
        {
            return;
        }
        byte current = _ctx.Engine.GetValues(_fixture.Id).ElementAtOrDefault(_color);
        if (current >= 25)
        {
            _lastColor = current;
            SetChannel(_color, 0);
            _ctx.Status($"{_fixture.Name} : faisceau éteint.");
        }
        else
        {
            EnsureDmxMode();
            SetChannel(_color, _lastColor);
            _ctx.Status($"{_fixture.Name} : faisceau allumé.");
        }
    }

    private void OnPad()
    {
        if (_updating)
        {
            return;
        }
        EnsureDmxMode();
        // Positions fixes : 0..127 sur chaque axe.
        _ctx.Engine.SetValue(_fixture.Id, _x, (byte)Math.Round(_pad.ValueX / 65535.0 * 127));
        _ctx.Engine.SetValue(_fixture.Id, _y, (byte)Math.Round(_pad.ValueY / 65535.0 * 127));
        RefreshFromEngine();
    }

    public void RefreshFromEngine()
    {
        if (IsDisposed)
        {
            return;
        }

        byte[] values = _ctx.Engine.GetValues(_fixture.Id);
        byte Value(int index) => index >= 0 && index < values.Length ? values[index] : (byte)0;

        _updating = true;
        try
        {
            bool beamOn = _color >= 0 && Value(_color) >= 25;
            _onOff.Text = beamOn ? "LASER ALLUMÉ\n(cliquer pour éteindre)" : "Laser éteint\n(cliquer pour allumer)";
            _onOff.BackColor = beamOn ? Color.FromArgb(40, 170, 60) : SystemColors.Control;
            _onOff.ForeColor = beamOn ? Color.White : SystemColors.ControlText;
            _onOff.UseVisualStyleBackColor = !beamOn;

            string mode = _mode >= 0
                ? _profile.Channels[_mode].Ranges.FirstOrDefault(r => r.Contains(Value(_mode)))?.Name ?? Value(_mode).ToString()
                : "?";
            _state.Text = $"Mode : {mode}";
            _state.ForeColor = TextKey.Normalize(mode).StartsWith("dmx") ? Ui.OkColor : Ui.WarningColor;

            Highlight(_modeButtons, b => _mode >= 0 && Equals(b.Tag, Value(_mode)));
            Highlight(_patternButtons, b => b.Tag is ChannelRange r && r.Contains(Value(_pattern)));
            foreach (var button in _colorButtons)
            {
                bool active = Equals(button.Tag, Value(_color));
                button.FlatAppearance.BorderSize = active ? 4 : 1;
                button.FlatAppearance.BorderColor = active ? Color.DeepSkyBlue : Color.Gray;
            }

            if (_x >= 0 && _y >= 0)
            {
                _pad.ValueX = Math.Min(127, (int)Value(_x)) * 65535 / 127;
                _pad.ValueY = Math.Min(127, (int)Value(_y)) * 65535 / 127;
            }
            foreach (var (index, control) in _rangeControls)
            {
                control.Show(Value(index));
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private static void Highlight(IEnumerable<Button> buttons, Func<Button, bool> isActive)
    {
        foreach (var button in buttons)
        {
            bool active = isActive(button);
            button.BackColor = active ? Color.LightSkyBlue : SystemColors.Control;
            button.UseVisualStyleBackColor = !active;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ctx.LiveStateChanged -= RefreshFromEngine;
        }
        base.Dispose(disposing);
    }
}
