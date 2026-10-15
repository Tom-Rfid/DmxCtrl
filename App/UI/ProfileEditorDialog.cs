// =====================================================================================
//  ProfileEditorDialog.cs
// -------------------------------------------------------------------------------------
//  Editeur de PROFILS (modules) : c'est ici qu'on cree un nouveau type d'appareil
//  ou qu'on corrige un profil existant (ex. l'ordre suppose des canaux de la 260X).
//
//  Pour chaque canal : nom, role (type), valeur par defaut, verifie, presets
//  ("Rouge=18; Vert=36"), notes. On peut importer/exporter un profil seul
//  (*.dmxprofile.json) pour le reutiliser dans une autre configuration.
//
//  On travaille sur des COPIES : rien ne change tant qu'on n'a pas clique sur OK.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;
using DmxController.Core.Storage;

namespace DmxController.UI;

[DesignerCategory("Code")]
internal sealed class ProfileEditorDialog : Form
{
    private sealed record TypeItem(string Value, string Text);

    private const int ColNum = 0, ColName = 1, ColType = 2, ColDefault = 3, ColVerified = 4, ColPresets = 5, ColNotes = 6;

    private readonly List<FixtureProfile> _profiles;
    private readonly Dictionary<string, List<string>> _usage; // id de profil -> noms des appareils qui l'utilisent
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true };
    private readonly TextBox _name = new() { Width = 420 };
    private readonly TextBox _manufacturer = new() { Width = 420 };
    private readonly TextBox _notes = new() { Width = 640, Height = 52, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = SystemColors.Window,
    };
    private FixtureProfile? _current;
    private bool _loading;

    /// <summary>Profils modifies (valides seulement si DialogResult == OK).</summary>
    public List<FixtureProfile> ResultProfiles => _profiles;

    public ProfileEditorDialog(ShowConfig show)
    {
        _profiles = show.Profiles.Select(p => ShowJson.Clone(p)).ToList();
        _usage = show.Fixtures
            .GroupBy(f => f.ProfileId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Name).ToList());

        Text = "Éditeur de profils (modules)";
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1200, 680);
        MinimumSize = new Size(960, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        // ---------------- Liste des profils (gauche) ----------------
        _list.Format += (_, e) =>
        {
            if (e.ListItem is FixtureProfile p)
            {
                e.Value = $"{p.Name} — {p.ChannelCount} canaux" + (p.IsFullyVerified ? "" : " (?)") +
                          (_usage.ContainsKey(p.Id) ? "  •" : "");
            }
        };
        foreach (var profile in _profiles)
        {
            _list.Items.Add(profile);
        }
        _list.SelectedIndexChanged += (_, _) => OnProfileSelected();

        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        leftButtons.Controls.Add(SmallButton("Nouveau…", NewProfile));
        leftButtons.Controls.Add(SmallButton("Dupliquer", DuplicateProfile));
        leftButtons.Controls.Add(SmallButton("Supprimer", DeleteProfile));
        leftButtons.Controls.Add(SmallButton("Importer…", ImportProfile));
        leftButtons.Controls.Add(SmallButton("Exporter…", ExportProfile));

        var left = new GroupBox { Text = "Profils  (•  = utilisé dans le patch)", Dock = DockStyle.Left, Width = 330, Padding = new Padding(8) };
        left.Controls.Add(_list);
        left.Controls.Add(leftButtons);

        // ---------------- Details du profil (droite) ----------------
        var fields = Ui.Table(2);
        Ui.Row(fields, "Nom", _name);
        Ui.Row(fields, "Fabricant", _manufacturer);
        Ui.Row(fields, "Notes", _notes);

        var typeColumn = new DataGridViewComboBoxColumn
        {
            HeaderText = "Rôle (type)",
            FillWeight = 150,
            DataSource = ChannelTypeInfo.All.Select(t => new TypeItem(t.ToString(), ChannelTypeInfo.DisplayName(t))).ToList(),
            ValueMember = nameof(TypeItem.Value),
            DisplayMember = nameof(TypeItem.Text),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        };
        _grid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "N°", FillWeight = 30, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Nom du canal", FillWeight = 140 },
            typeColumn,
            new DataGridViewTextBoxColumn { HeaderText = "Défaut", FillWeight = 45 },
            new DataGridViewCheckBoxColumn { HeaderText = "Vérifié", FillWeight = 45 },
            new DataGridViewTextBoxColumn { HeaderText = "Presets (Nom=valeur; …)", FillWeight = 200 },
            new DataGridViewTextBoxColumn { HeaderText = "Notes", FillWeight = 150 });
        _grid.Columns[ColNum].DefaultCellStyle.BackColor = SystemColors.Control;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.DataError += (_, e) => e.ThrowException = false;

        var channelButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        channelButtons.Controls.Add(SmallButton("Ajouter un canal", () => InsertChannel(atEnd: true)));
        channelButtons.Controls.Add(SmallButton("Insérer avant", () => InsertChannel(atEnd: false)));
        channelButtons.Controls.Add(SmallButton("Supprimer le canal", RemoveChannel));
        channelButtons.Controls.Add(SmallButton("Monter", () => MoveChannel(-1)));
        channelButtons.Controls.Add(SmallButton("Descendre", () => MoveChannel(+1)));
        channelButtons.Controls.Add(SmallButton("Tout marquer vérifié", MarkAllVerified));
        channelButtons.Controls.Add(new Label
        {
            Text = "L'ordre des lignes = l'ordre des canaux DMX de l'appareil. Changer le nombre de canaux décale les adresses : vérifie le patch ensuite.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(8, 6, 3, 3),
        });

        var right = new GroupBox { Text = "Profil sélectionné", Dock = DockStyle.Fill, Padding = new Padding(8) };
        right.Controls.Add(_grid);
        right.Controls.Add(channelButtons);
        right.Controls.Add(fields);

        // ---------------- OK / Annuler ----------------
        var ok = new Button { Text = "OK", Width = 90, Height = 30 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Annuler", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        bottom.Controls.AddRange(new Control[] { cancel, ok });
        CancelButton = cancel;

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(bottom);

        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
        else
        {
            LoadProfile(null);
        }
    }

    private static Button SmallButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(2) };
        button.Click += (_, _) => action();
        return button;
    }

    // =================================================================================
    //  Selection / chargement / validation d'un profil
    // =================================================================================

    private void OnProfileSelected()
    {
        if (_loading)
        {
            return;
        }

        var next = _list.SelectedItem as FixtureProfile;
        if (ReferenceEquals(next, _current))
        {
            return;
        }

        // On valide le profil en cours avant de passer au suivant ; en cas d'erreur on reste dessus.
        if (!CommitCurrent())
        {
            _loading = true;
            _list.SelectedItem = _current;
            _loading = false;
            return;
        }

        LoadProfile(next);
        RefreshListText();
    }

    private void LoadProfile(FixtureProfile? profile)
    {
        _current = profile;
        _loading = true;
        try
        {
            _grid.Rows.Clear();
            bool enabled = profile is not null;
            _name.Enabled = _manufacturer.Enabled = _notes.Enabled = _grid.Enabled = enabled;
            _name.Text = profile?.Name ?? "";
            _manufacturer.Text = profile?.Manufacturer ?? "";
            _notes.Text = profile?.Notes ?? "";
            if (profile is not null)
            {
                foreach (var channel in profile.Channels)
                {
                    AddChannelRow(channel, null);
                }
            }
            RenumberRows();
        }
        finally
        {
            _loading = false;
        }
    }

    private void AddChannelRow(ChannelDef channel, int? insertAt)
    {
        int index;
        if (insertAt is int at)
        {
            _grid.Rows.Insert(at, 1);
            index = at;
        }
        else
        {
            index = _grid.Rows.Add();
        }

        var row = _grid.Rows[index];
        row.Cells[ColName].Value = channel.Name;
        row.Cells[ColType].Value = channel.Type.ToString();
        row.Cells[ColDefault].Value = channel.Default.ToString();
        row.Cells[ColVerified].Value = channel.Verified;
        row.Cells[ColPresets].Value = ChannelPreset.Format(channel.Presets);
        row.Cells[ColNotes].Value = channel.Notes;
    }

    private void RenumberRows()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Cells[ColNum].Value = (row.Index + 1).ToString();
        }
    }

    /// <summary>Recopie le contenu de l'editeur dans le profil courant. false (+ message) si invalide.</summary>
    private bool CommitCurrent()
    {
        if (_current is null)
        {
            return true;
        }

        _grid.EndEdit();
        var errors = new List<string>();
        var channels = new List<ChannelDef>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            int number = row.Index + 1;
            string name = (Convert.ToString(row.Cells[ColName].Value) ?? "").Trim();
            if (!Enum.TryParse(Convert.ToString(row.Cells[ColType].Value), out ChannelType type))
            {
                type = ChannelType.Generic;
            }

            string defaultText = (Convert.ToString(row.Cells[ColDefault].Value) ?? "0").Trim();
            if (!int.TryParse(defaultText.Length == 0 ? "0" : defaultText, out int defaultValue) || defaultValue is < 0 or > 255)
            {
                errors.Add($"Canal {number} : valeur par défaut « {defaultText} » invalide (0 à 255).");
                defaultValue = 0;
            }

            List<ChannelPreset> presets;
            try
            {
                presets = ChannelPreset.Parse(Convert.ToString(row.Cells[ColPresets].Value));
            }
            catch (FormatException ex)
            {
                errors.Add($"Canal {number} : {ex.Message}");
                presets = new();
            }

            channels.Add(new ChannelDef
            {
                Name = name.Length > 0 ? name : $"Canal {number}",
                Type = type,
                Default = (byte)defaultValue,
                Verified = row.Cells[ColVerified].Value is true,
                Presets = presets,
                Notes = (Convert.ToString(row.Cells[ColNotes].Value) ?? "").Trim(),
            });
        }

        if (channels.Count == 0)
        {
            errors.Add("Un profil doit avoir au moins un canal.");
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, $"Le profil « {_name.Text} » contient des erreurs :\n\n" + string.Join("\n", errors),
                "Profil invalide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _current.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Module sans nom" : _name.Text.Trim();
        _current.Manufacturer = _manufacturer.Text.Trim();
        _current.Notes = _notes.Text.Trim();
        _current.Channels = channels;
        return true;
    }

    /// <summary>Force la ListBox a redessiner le texte de ses elements (noms modifies).</summary>
    private void RefreshListText()
    {
        _loading = true;
        try
        {
            var selected = _list.SelectedItem;
            for (int i = 0; i < _list.Items.Count; i++)
            {
                _list.Items[i] = _list.Items[i];
            }
            _list.SelectedItem = selected;
        }
        finally
        {
            _loading = false;
        }
    }

    private void AddAndSelect(FixtureProfile profile)
    {
        _profiles.Add(profile);
        _list.Items.Add(profile);
        _list.SelectedItem = profile; // -> OnProfileSelected (le profil courant est valide d'abord)
    }

    // =================================================================================
    //  Actions sur les profils
    // =================================================================================

    private void NewProfile()
    {
        if (!CommitCurrent())
        {
            return;
        }

        string? name = InputDialog.Ask(this, "Nouveau profil", "Nom du nouveau module (ex. « Par LED RGBW 8 canaux ») :");
        if (name is null)
        {
            return;
        }
        string? countText = InputDialog.Ask(this, "Nouveau profil",
            "Nombre de canaux DMX (voir la notice de l'appareil, dans le mode DMX choisi) :", "8");
        if (countText is null || !int.TryParse(countText, out int count) || count is < 1 or > 512)
        {
            MessageBox.Show(this, "Nombre de canaux invalide (1 à 512).", "Nouveau profil", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var profile = new FixtureProfile { Name = name };
        for (int i = 1; i <= count; i++)
        {
            profile.Channels.Add(new ChannelDef { Name = $"Canal {i}" });
        }
        AddAndSelect(profile);
    }

    private void DuplicateProfile()
    {
        if (_current is null || !CommitCurrent())
        {
            return;
        }
        var copy = ShowJson.Clone(_current);
        copy.Id = FixtureProfile.NewId();
        copy.Name += " (copie)";
        AddAndSelect(copy);
    }

    private void DeleteProfile()
    {
        if (_current is null)
        {
            return;
        }
        if (_usage.TryGetValue(_current.Id, out var users))
        {
            MessageBox.Show(this, $"Ce profil est utilisé par : {string.Join(", ", users)}.\n" +
                                  "Change ou supprime d'abord ces appareils dans le patch.",
                "Profil utilisé", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Ui.Confirm(this, $"Supprimer le profil « {_current.Name} » ?"))
        {
            return;
        }

        var doomed = _current;
        _current = null; // pas de validation du profil supprime
        _profiles.Remove(doomed);
        _list.Items.Remove(doomed);
        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
        else
        {
            LoadProfile(null);
        }
    }

    private void ImportProfile()
    {
        if (!CommitCurrent())
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Importer un profil",
            Filter = $"Profil d'appareil (*{ShowStore.ProfileExtension})|*{ShowStore.ProfileExtension}|Fichiers JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var profile = ShowStore.LoadProfile(dialog.FileName);
            if (_profiles.Any(p => p.Id == profile.Id))
            {
                profile.Id = FixtureProfile.NewId();
                profile.Name += " (importé)";
            }
            AddAndSelect(profile);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible de lire ce profil :\n{ex.Message}", "Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportProfile()
    {
        if (_current is null || !CommitCurrent())
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Exporter le profil",
            Filter = $"Profil d'appareil (*{ShowStore.ProfileExtension})|*{ShowStore.ProfileExtension}",
            FileName = SafeFileName(_current.Name) + ShowStore.ProfileExtension,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ShowStore.SaveProfile(_current, dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible d'enregistrer :\n{ex.Message}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Trim().Length == 0 ? "sans-nom" : clean.Trim();
    }

    // =================================================================================
    //  Actions sur les canaux
    // =================================================================================

    private void InsertChannel(bool atEnd)
    {
        if (_current is null)
        {
            return;
        }
        int? at = atEnd || _grid.CurrentRow is null ? null : _grid.CurrentRow.Index;
        AddChannelRow(new ChannelDef { Name = $"Canal {_grid.Rows.Count + 1}" }, at);
        RenumberRows();
    }

    private void RemoveChannel()
    {
        if (_grid.CurrentRow is DataGridViewRow row && _grid.Rows.Count > 1)
        {
            _grid.Rows.Remove(row);
            RenumberRows();
        }
    }

    private void MoveChannel(int delta)
    {
        if (_grid.CurrentRow is not DataGridViewRow row)
        {
            return;
        }
        int index = row.Index;
        int target = index + delta;
        if (target < 0 || target >= _grid.Rows.Count)
        {
            return;
        }
        _grid.EndEdit();
        _grid.Rows.RemoveAt(index);
        _grid.Rows.Insert(target, row);
        _grid.ClearSelection();
        row.Selected = true;
        _grid.CurrentCell = row.Cells[ColName];
        RenumberRows();
    }

    private void MarkAllVerified()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Cells[ColVerified].Value = true;
        }
    }

    private void Accept()
    {
        if (!CommitCurrent())
        {
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
