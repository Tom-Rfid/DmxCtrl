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
    private readonly ComboBox _ranges = new() { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 3, 3) };
    private readonly TrackBar _slider = Ui.Slider(0, 100, 200);
    private readonly Label _value = Ui.ValueLabel(40);
    private bool _updating;

    /// <summary>Leve quand l'utilisateur change la valeur.</summary>
    public event Action<byte>? ValueChanged;

    public RangeControl(ChannelDef channel, int listWidth = 300)
    {
        _channel = channel;
        _ranges.Width = listWidth;
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
    private readonly int _zoom;

    private readonly Button _onOff = new() { Size = new Size(260, 56), Font = Ui.BigFont, Margin = new Padding(6) };
    private readonly Label _state = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(6, 8, 3, 3) };
    private readonly List<Button> _modeButtons = new();
    private readonly List<Button> _patternButtons = new();
    private readonly List<Button> _riskyButtons = new();

    // ----- Section Couleur : type (eteint / fixe / alternance / defilement) + choix + un curseur -----
    private enum ColorKind { Off, Fixed, Alternate, Continuous, Other }

    private readonly int _blink; // canal "Clignotement" (strobe en couleur fixe, vitesse de changement sinon)
    private readonly Dictionary<ColorKind, CheckBox> _kindButtons = new();
    private readonly FlowLayoutPanel _colorChoices = Ui.Flow(440);
    private readonly Label _speedLabel = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 8, 3, 3) };
    private readonly TrackBar _speed = Ui.Slider(0, 100, 220);
    private readonly Label _speedValue = Ui.ValueLabel(80);
    private ColorKind _shownKind = (ColorKind)(-1);
    private readonly XYPad _pad = new() { Size = new Size(240, 240), XLabel = "X →", YLabel = "Y ↓" };
    private readonly List<(int Index, RangeControl Control)> _rangeControls = new();
    private byte _lastColor = 62; // couleur rendue a l'allumage (rouge) si aucune autre n'a ete choisie
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
        _blink = profile.IndexOf(ChannelType.Strobe);
        _zoom = profile.IndexOf(ChannelType.LaserZoom);

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

        // ---------------- En-tete + securite : bouton marche / arret a gauche, textes a droite ----------------
        var header = Ui.Table(1);
        header.Margin = new Padding(0);
        header.Dock = DockStyle.None;
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
        header.Controls.Add(_state);
        _onOff.Click += (_, _) => ToggleBeam();
        _onOff.Margin = new Padding(3, 3, 12, 3);
        page.Controls.Add(Ui.Flow(0, _onOff, header));

        // ---------------- Mode ----------------
        if (_mode >= 0)
        {
            var modes = Ui.Flow(0); // une seule ligne (avec retour a la ligne, la hauteur calculee serait fausse)
            foreach (var preset in profile.Channels[_mode].Presets)
            {
                var button = new Button { Text = preset.Name, AutoSize = true, Tag = preset.Value, Margin = new Padding(2, 0, 2, 0) };
                _modeButtons.Add(button);
                if (preset.Name.Contains('⚠'))
                {
                    // Bouton a risque (« Son rouge » met ce laser en mode test) : confirmation, et
                    // range a part, en bas a droite de la page.
                    button.ForeColor = Ui.WarningColor;
                    button.Font = Ui.BoldFont;
                    button.Click += (_, _) => ConfirmRiskyMode(preset);
                    _riskyButtons.Add(button);
                    continue;
                }
                button.Click += (_, _) => SetChannel(_mode, preset.Value);
                modes.Controls.Add(button);
            }
            var group = Ui.Group("Mode (programmes internes ou DMX manuel)", modes);
            group.Padding = new Padding(6, 4, 6, 3); // boite compacte : une seule ligne de boutons
            page.Controls.Add(group);
        }

        // ---------------- Motifs | couleurs + position (cote a cote) ----------------
        var middle = Ui.Flow(0);
        if (_pattern >= 0)
        {
            // 2 colonnes (1-16 | 17-32) : bouton numero + mini dessin + legende du motif.
            var grid = Ui.Table(6);
            grid.Dock = DockStyle.None;
            var ranges = profile.Channels[_pattern].Ranges;
            int rows = (ranges.Count + 1) / 2;
            for (int n = 0; n < ranges.Count; n++)
            {
                var range = ranges[n];
                var button = new Button { Text = (n + 1).ToString(), Size = new Size(40, 26), Tag = range, Margin = new Padding(2) };
                button.Click += (_, _) =>
                {
                    EnsureDmxMode();
                    SetChannel(_pattern, range.At(0.5));
                };
                _patternButtons.Add(button);
                int colon = range.Name.IndexOf(" : ", StringComparison.Ordinal);
                var legend = new Label
                {
                    Text = colon >= 0 ? range.Name[(colon + 3)..] : "",
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(2, 0, 14, 0),
                };
                var icon = new LaserPatternIcon(n);
                icon.Click += (_, _) => button.PerformClick();
                int column = n < rows ? 0 : 3;
                grid.Controls.Add(button, column, n % rows);
                grid.Controls.Add(icon, column + 1, n % rows);
                grid.Controls.Add(legend, column + 2, n % rows);
            }
            middle.Controls.Add(Ui.Group("Motif (32 motifs)", grid));
        }

        // ---------------- Zoom, puis couleurs | position ----------------
        var right = Ui.Table(1);
        if (_zoom >= 0)
        {
            // Copie du curseur Zoom (aussi dans « Effets ») : sous la main, en haut.
            right.Controls.Add(Ui.Group("Zoom", BuildZoomControl(listWidth: 190)));
        }
        var colorAndPad = Ui.Flow(0);
        right.Controls.Add(colorAndPad);
        if (_color >= 0)
        {
            colorAndPad.Controls.Add(Ui.Group("Couleur", BuildColorSection()));
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
            colorAndPad.Controls.Add(Ui.Group("Position", padBox));
        }
        right.Dock = DockStyle.None;
        middle.Controls.Add(right);
        page.Controls.Add(middle);

        // ---------------- Autres canaux : plage + curseur ----------------
        var others = Ui.Table(3);
        for (int i = 0; i < profile.ChannelCount; i++)
        {
            var channel = profile.Channels[i];
            if (i == _mode || i == _pattern || i == _color || i == _blink || channel.Ranges.Count == 0)
            {
                continue; // le clignotement est dans la section Couleur
            }
            if (i == _zoom)
            {
                Ui.Row(others, $"{channel.Name}  [DMX {fixture.Address + i}]", BuildZoomControl(listWidth: 300));
                continue;
            }
            int index = i;
            var control = new RangeControl(channel);
            control.ValueChanged += value => SetChannel(index, value);
            _rangeControls.Add((i, control));
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

        // Boutons a risque (mode test) : en bas a droite, a l'ecart.
        if (_riskyButtons.Count > 0)
        {
            var risky = Ui.Flow(0, _riskyButtons.Cast<Control>().Prepend(Ui.Text("Coupe le DMX du laser (mode test) :", color: Ui.WarningColor)).ToArray());
            risky.Anchor = AnchorStyles.Right;
            page.Controls.Add(risky);
        }

        Controls.Add(scroll);

        _ctx.LiveStateChanged += RefreshFromEngine;
        RefreshFromEngine();
    }

    // =================================================================================
    //  Section Couleur
    //  Notice, canal 3 : 0-24 eteint, 25-49 couleurs d'origine, 50-124 couleur FIXE,
    //  125-224 ALTERNANCES, 225-255 DEFILEMENT continu. Le canal 4 depend du canal 3 :
    //  couleur fixe -> vitesse du CLIGNOTEMENT ; alternance / defilement -> VITESSE DU
    //  CHANGEMENT de couleur (0-24 = arret : sans lui, une alternance reste figee).
    // =================================================================================

    /// <summary>Curseur du zoom + bouton de retour a 60 (sous 60 le motif se deforme sur ce laser).</summary>
    private Control BuildZoomControl(int listWidth)
    {
        var control = new RangeControl(_profile.Channels[_zoom], listWidth);
        control.ValueChanged += value => SetChannel(_zoom, value);
        _rangeControls.Add((_zoom, control));
        var reset = new Button { Text = $"{FixtureLibrary.ZoomDefault}", AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
        reset.Click += (_, _) => SetChannel(_zoom, FixtureLibrary.ZoomDefault);
        return Ui.Flow(0, control, reset);
    }

    private static ColorKind KindOf(byte color) => color switch
    {
        <= 24 => ColorKind.Off,
        <= 49 => ColorKind.Other,
        <= 124 => ColorKind.Fixed,
        <= 224 => ColorKind.Alternate,
        _ => ColorKind.Continuous,
    };

    private Control BuildColorSection()
    {
        var kinds = Ui.Flow(0);
        foreach (var (kind, text) in new[]
                 {
                     (ColorKind.Off, "Éteint"), (ColorKind.Fixed, "Fixe"),
                     (ColorKind.Alternate, "Alternance"), (ColorKind.Continuous, "Défilement continu"),
                 })
        {
            var box = new CheckBox
            {
                Text = text,
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(104, 32),
                Font = Ui.BoldFont,
                Margin = new Padding(3),
            };
            box.Click += (_, _) => SelectKind(kind);
            _kindButtons[kind] = box;
            kinds.Controls.Add(box);
        }

        _speed.ValueChanged += (_, _) => OnSpeedMoved();
        var table = Ui.Table(1);
        table.Controls.Add(kinds);
        table.Controls.Add(_colorChoices);
        table.Controls.Add(Ui.Flow(0, _speedLabel, _speed, _speedValue));
        table.Controls.Add(new Label
        {
            Text = "Fixe : le curseur règle le clignotement. Alternance / défilement : il règle la vitesse de changement " +
                   "(à 0 l'alternance reste figée).",
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 4, 3, 3),
        });
        return table;
    }

    /// <summary>
    /// Choix du type : premiere couleur du type ; en alternance / defilement, le canal 4 est mis a
    /// une vitesse moyenne s'il etait a l'arret ; en fixe, le clignotement est coupe.
    /// </summary>
    private void SelectKind(ColorKind kind)
    {
        EnsureDmxMode();
        byte[] values = _ctx.Engine.GetValues(_fixture.Id);
        byte current = values.ElementAtOrDefault(_color);
        if (kind == ColorKind.Off)
        {
            if (current >= 25)
            {
                _lastColor = current;
            }
            SetChannel(_color, 0);
            return;
        }

        if (KindOf(current) != kind)
        {
            var first = RangesOf(kind).FirstOrDefault();
            _ctx.Engine.SetValue(_fixture.Id, _color, first?.At(0.5) ?? (byte)240);
        }
        if (_blink >= 0)
        {
            byte blink = values.ElementAtOrDefault(_blink);
            if (kind == ColorKind.Fixed && KindOf(current) != ColorKind.Fixed)
            {
                _ctx.Engine.SetValue(_fixture.Id, _blink, 0);   // pas de clignotement
            }
            else if (kind != ColorKind.Fixed && blink < 25)
            {
                _ctx.Engine.SetValue(_fixture.Id, _blink, 140); // sinon l'alternance reste figee
            }
        }
        RefreshFromEngine();
    }

    /// <summary>Plages de la notice pour un type de couleur (noms du profil : « Rouge + vert ensemble »...).</summary>
    private IEnumerable<ChannelRange> RangesOf(ColorKind kind) =>
        _profile.Channels[_color].Ranges.Where(r => KindOf(r.From) == kind && kind is ColorKind.Fixed or ColorKind.Alternate);

    /// <summary>Boutons des couleurs du type affiche (recrees seulement quand le type change).</summary>
    private void RebuildColorChoices(ColorKind kind)
    {
        _shownKind = kind;
        _colorChoices.SuspendLayout();
        foreach (Control old in _colorChoices.Controls.Cast<Control>().ToList())
        {
            old.Dispose();
        }
        _colorChoices.Controls.Clear();
        foreach (var range in RangesOf(kind))
        {
            string name = range.Name.Replace("Alternance ", "");
            var button = new Button { Text = name, Size = new Size(140, 34), Tag = range, Margin = new Padding(3), FlatStyle = FlatStyle.Flat };
            button.BackColor = LaserColorFor(range.Name);
            button.ForeColor = button.BackColor.GetBrightness() < 0.45f ? Color.White : Color.Black;
            button.Click += (_, _) =>
            {
                EnsureDmxMode();
                SetChannel(_color, range.At(0.5));
            };
            _colorChoices.Controls.Add(button);
        }
        if (_colorChoices.Controls.Count == 0)
        {
            _colorChoices.Controls.Add(Ui.Text(kind switch
            {
                ColorKind.Off => "Laser éteint.",
                ColorKind.Continuous => "Les couleurs défilent toutes seules.",
                _ => "Couleurs d'origine du motif.",
            }, color: Color.DimGray));
        }
        _colorChoices.ResumeLayout();
    }

    private void RefreshColorSection(byte color, byte blink)
    {
        if (_color < 0)
        {
            return;
        }
        var kind = KindOf(color);
        foreach (var (k, box) in _kindButtons)
        {
            box.Checked = k == kind;
            box.BackColor = k == kind ? Color.LightSkyBlue : SystemColors.Control;
            box.UseVisualStyleBackColor = k != kind;
        }
        if (kind != _shownKind)
        {
            RebuildColorChoices(kind);
        }
        foreach (Control control in _colorChoices.Controls)
        {
            if (control is Button { Tag: ChannelRange range } button)
            {
                bool active = range.Contains(color);
                button.FlatAppearance.BorderSize = active ? 4 : 1;
                button.FlatAppearance.BorderColor = active ? Color.DeepSkyBlue : Color.Gray;
            }
        }

        // Un seul curseur pour le canal 4, nomme selon le type de couleur.
        _speedLabel.Text = kind == ColorKind.Fixed ? "Clignotement" : "Vitesse de changement";
        _speed.Enabled = _blink >= 0 && kind != ColorKind.Off;
        _speed.Value = blink < 25 ? 0 : Math.Clamp((int)Math.Round((blink - 25) * 99.0 / 230) + 1, 1, 100);
        _speedValue.Text = blink < 25 ? (kind == ColorKind.Fixed ? "aucun" : "arrêt") : $"{_speed.Value} %";
    }

    /// <summary>Curseur 0..100 % -> canal 4 : 0 = arret (0), 1..100 % = 25..255 (lent -> rapide).</summary>
    private void OnSpeedMoved()
    {
        if (_updating || _blink < 0)
        {
            return;
        }
        int v = _speed.Value;
        SetChannel(_blink, v == 0 ? (byte)0 : (byte)Math.Round(25 + (v - 1) * 230.0 / 99));
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
            if (_zoom >= 0)
            {
                _ctx.Engine.SetValue(_fixture.Id, _zoom, FixtureLibrary.ZoomDefault); // taille nette a l'allumage
            }
            SetChannel(_color, _lastColor);
            _ctx.Status($"{_fixture.Name} : faisceau allumé (zoom {FixtureLibrary.ZoomDefault}).");
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
            RefreshColorSection(Value(_color), Value(_blink));

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
