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
    private readonly Label[] _values;
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

    private readonly CheckBox? _noStrobe; // "Pas de strobe" (si le profil a un canal Strobe)
    private readonly Label _warning = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 6, 3, 3) };
    private readonly ToolTip _tips = new();
    private bool _updating;

    public FixturePanel(ShowContext ctx, FixtureInstance fixture, FixtureProfile profile)
    {
        _ctx = ctx;
        _fixture = fixture;
        _profile = profile;
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.None;

        int count = profile.ChannelCount;
        _sliders = new TrackBar[count];
        _values = new Label[count];
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
            left = new Panel { Dock = DockStyle.Left, Width = 400, Padding = new Padding(10) };

            (_groupLeft, _groupRight) = LedGroups.Split(profile);
            if (_groupLeft.Length > 0 && _groupRight.Length > 0)
            {
                _ledLeft = new VerticalFader { Location = new Point(284, 6), Size = new Size(48, 260) };
                _ledRight = new VerticalFader { Location = new Point(340, 6), Size = new Size(48, 260) };
                _ledLeft.ValueChanged += (_, _) => OnLedFader(0);
                _ledRight.ValueChanged += (_, _) => OnLedFader(1);
                _tips.SetToolTip(_ledLeft, $"LED de l'aile gauche ({LedNames(_groupLeft)}) : bas = 0 %, haut = 100 %.\nGarde la couleur en cours (les proportions entre LED).");
                _tips.SetToolTip(_ledRight, $"LED de l'aile droite ({LedNames(_groupRight)}) : bas = 0 %, haut = 100 %.\nGarde la couleur en cours (les proportions entre LED).");
                left.Controls.Add(_ledLeft);
                left.Controls.Add(_ledRight);
                left.Controls.Add(new Label { Text = "LED\ngauche", Location = new Point(276, 270), Size = new Size(64, 46), TextAlign = ContentAlignment.TopCenter });
                left.Controls.Add(new Label { Text = "LED\ndroite", Location = new Point(332, 270), Size = new Size(64, 46), TextAlign = ContentAlignment.TopCenter });
            }
            _wings = new XYPad
            {
                Location = new Point(12, 6),
                Size = new Size(260, 260),
                XLabel = "Ailes ← gauche · droite →",
                YLabel = _dimmer >= 0 ? "Dimmer : haut = 100 %, bas = 0" : "",
            };
            _wings.ValueChanged += OnWingsChanged;

            _mirrorWings = new CheckBox
            {
                Text = "Inverser (ailes en miroir)",
                Checked = fixture.MirrorMotors,
                Location = new Point(12, 272),
                AutoSize = true,
            };
            _mirrorWings.CheckedChanged += (_, _) => OnMirrorChanged();

            var center = new Button { Text = "Ailes au milieu", Location = new Point(12, 298), Width = 126, Height = 30 };
            center.Click += (_, _) =>
            {
                _ctx.Engine.SetMotors(_fixture.Id, 128, _mirrorWings.Checked);
                RefreshFromEngine();
            };
            var reset = new Button { Text = "Par défaut", Location = new Point(146, 298), Width = 126, Height = 30 };
            reset.Click += (_, _) => ResetDefaults();

            _wingsInfo = new Label { Location = new Point(12, 334), Size = new Size(260, 20), Font = Ui.BoldFont };
            var hint = new Label
            {
                Text = "Gauche / droite : les 2 ailes ensemble. Avec « Inverser », elles partent " +
                       "chacune de leur côté et reviennent ensemble au milieu.\nHaut / bas : dimmer.",
                Location = new Point(12, 358),
                Size = new Size(260, 80),
                ForeColor = Color.DimGray,
            };
            left.Controls.AddRange(new Control[] { _wings, _mirrorWings, center, reset, _wingsInfo, hint });
        }
        else
        {
            var reset = new Button { Text = "Valeurs par défaut", AutoSize = true };
            reset.Click += (_, _) => ResetDefaults();
            header.Controls.Add(reset);
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

            _values[i] = Ui.ValueLabel(36);

            _presets[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(3, 4, 3, 3) };
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

            // Case "Pas de strobe" a cote du (premier) canal Strobe : force ce canal a 0 en sortie.
            if (channel.Type == ChannelType.Strobe && _noStrobe is null)
            {
                _noStrobe = new CheckBox
                {
                    Text = "Pas de strobe",
                    AutoSize = true,
                    Checked = fixture.NoStrobe,
                    Font = Ui.BoldFont,
                    Margin = new Padding(10, 6, 3, 3),
                };
                _tips.SetToolTip(_noStrobe, "Coché : le strobe de cet appareil reste coupé, même si une scène ou le curseur en demande un.");
                _noStrobe.CheckedChanged += (_, _) => OnNoStrobeChanged();
                table.Controls.Add(_noStrobe, 6, i);
            }
        }
        scroll.Controls.Add(table);
        UpdateStrobeLock();

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
                _values[i].Text = values[i].ToString();
                SyncPresetCombo(i, values[i]);
            }
            SyncPads(values);
        }
        finally
        {
            _updating = false;
        }
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

    private void OnSlider(int index)
    {
        if (_updating)
        {
            return;
        }

        byte value = (byte)_sliders[index].Value;
        _values[index].Text = value.ToString();
        _ctx.Engine.SetValue(_fixture.Id, index, value);
        SyncPresetCombo(index, value);

        var type = _profile.Channels[index].Type;
        if (type is ChannelType.Pan or ChannelType.PanFine or ChannelType.Tilt or ChannelType.TiltFine
            or ChannelType.Motor or ChannelType.Dimmer || ChannelTypeInfo.IsColorMix(type))
        {
            SyncPads(_ctx.Engine.GetValues(_fixture.Id));
        }
    }

    /// <summary>Recale les pads (lyre et ailes) sur les valeurs actuelles des canaux.</summary>
    private void SyncPads(byte[] values)
    {
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
                _values[i].Text = values[i].ToString();
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private string LedNames(int[] group) => string.Join(", ", group.Select(i => _profile.Channels[i].Name));

    private void OnNoStrobeChanged()
    {
        if (_updating || _noStrobe is null)
        {
            return;
        }

        bool noStrobe = _noStrobe.Checked;
        _ctx.Engine.EditShow(_ => _fixture.NoStrobe = noStrobe);
        _ctx.MarkDirty();
        if (noStrobe)
        {
            // On remet aussi le curseur a 0 : sinon il afficherait une valeur qui n'est pas envoyee.
            for (int i = 0; i < _profile.ChannelCount; i++)
            {
                if (_profile.Channels[i].Type == ChannelType.Strobe)
                {
                    _ctx.Engine.SetValue(_fixture.Id, i, 0);
                }
            }
        }
        UpdateStrobeLock();
        RefreshFromEngine();
        _ctx.Status(noStrobe ? $"{_fixture.Name} : strobe coupé." : $"{_fixture.Name} : strobe de nouveau autorisé.");
    }

    /// <summary>Grise le(s) curseur(s) Strobe quand "Pas de strobe" est coche.</summary>
    private void UpdateStrobeLock()
    {
        bool locked = _noStrobe?.Checked == true;
        for (int i = 0; i < _profile.ChannelCount; i++)
        {
            if (_profile.Channels[i].Type == ChannelType.Strobe)
            {
                _sliders[i].Enabled = !locked;
            }
        }
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
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
