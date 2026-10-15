// =====================================================================================
//  FixturePanel.cs
// -------------------------------------------------------------------------------------
//  Onglet d'UN appareil, genere automatiquement a partir de son profil :
//    - en-tete : nom, profil, plage d'adresses, avertissement si canaux non verifies
//    - pad XY pour orienter la lyre (si le profil a pan/tilt)
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
            var reset = new Button { Text = "Valeurs par défaut", Location = new Point(146, 274), Width = 126, Height = 30 };
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
            ColumnCount = 6,
            Location = new Point(6, 4),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        for (int c = 0; c < 6; c++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        for (int i = 0; i < count; i++)
        {
            int index = i; // capture pour les lambdas
            var channel = profile.Channels[i];

            _names[i] = new Label { AutoSize = false, Width = 260, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
            _tips.SetToolTip(_names[i], $"{ChannelTypeInfo.DisplayName(channel.Type)}" +
                                        (string.IsNullOrWhiteSpace(channel.Notes) ? "" : $"\n{channel.Notes}"));

            _sliders[i] = Ui.Slider(0, 255, 330);
            _sliders[i].ValueChanged += (_, _) => OnSlider(index);

            _values[i] = Ui.ValueLabel(36);

            _presets[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(3, 4, 3, 3) };
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
                _values[i].Text = values[i].ToString();
                SyncPresetCombo(i, values[i]);
            }
            if (_xy is not null)
            {
                _xy.ValueX = ShowEngine.GetAxis16(_profile, values, ChannelType.Pan, ChannelType.PanFine);
                _xy.ValueY = ShowEngine.GetAxis16(_profile, values, ChannelType.Tilt, ChannelType.TiltFine);
            }
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
        if (_xy is not null && type is ChannelType.Pan or ChannelType.PanFine or ChannelType.Tilt or ChannelType.TiltFine)
        {
            byte[] values = _ctx.Engine.GetValues(_fixture.Id);
            _xy.ValueX = ShowEngine.GetAxis16(_profile, values, ChannelType.Pan, ChannelType.PanFine);
            _xy.ValueY = ShowEngine.GetAxis16(_profile, values, ChannelType.Tilt, ChannelType.TiltFine);
        }
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
