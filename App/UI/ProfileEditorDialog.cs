// =====================================================================================
//  ProfileEditorDialog.cs
// -------------------------------------------------------------------------------------
//  PROFILE (module) editor: this is where a new kind of fixture is created or an
//  existing profile is fixed (e.g. the assumed channel order of the 260X).
//
//  For each channel: name, role (type), default value, checked, presets
//  ("Rouge=18; Vert=36"), notes. A single profile can be imported/exported
//  (*.dmxprofile.json) to reuse it in another configuration.
//
//  The dialog works on COPIES: nothing changes until OK is clicked.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;
using DmxController.Core.Storage;

namespace DmxController.UI;

/// <summary>Profile (module) editor: profile list on the left, channels of the selected profile on the right.</summary>
[DesignerCategory("Code")]
internal sealed class ProfileEditorDialog : Form
{
    /// <summary>Item of the channel type list: enum value and displayed label.</summary>
    private sealed record TypeItem(string Value, string Text);

    private const int ColNum = 0, ColName = 1, ColType = 2, ColDefault = 3, ColVerified = 4, ColPresets = 5, ColNotes = 6;

    private readonly List<FixtureProfile> _profiles;
    private readonly Dictionary<string, List<string>> _usage; // profile id -> names of the fixtures using it
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

    /// <summary>Edited profiles (valid only when DialogResult == OK).</summary>
    public List<FixtureProfile> ResultProfiles => _profiles;

    /// <summary>Opens the editor on copies of the profiles of <paramref name="show"/>.</summary>
    public ProfileEditorDialog(ShowConfig show)
    {
        _profiles = show.Profiles.Select(p => ShowJson.Clone(p)).ToList();
        _usage = show.Fixtures
            .GroupBy(f => f.ProfileId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Name).ToList());

        Text = Tr.T("Éditeur de profils (modules)");
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1200, 680);
        MinimumSize = new Size(960, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        // ---------------- Profile list (left) ----------------
        _list.Format += (_, e) =>
        {
            if (e.ListItem is FixtureProfile p)
            {
                e.Value = Tr.F("{0} — {1} canaux", Tr.T(p.Name), p.ChannelCount) + (p.IsFullyVerified ? "" : " (?)") +
                          (_usage.ContainsKey(p.Id) ? "  •" : "");
            }
        };
        foreach (var profile in _profiles)
        {
            _list.Items.Add(profile);
        }
        _list.SelectedIndexChanged += (_, _) => OnProfileSelected();

        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        leftButtons.Controls.Add(SmallButton(Tr.T("Nouveau…"), NewProfile));
        leftButtons.Controls.Add(SmallButton(Tr.T("Dupliquer"), DuplicateProfile));
        leftButtons.Controls.Add(SmallButton(Tr.T("Supprimer"), DeleteProfile));
        leftButtons.Controls.Add(SmallButton(Tr.T("Importer…"), ImportProfile));
        leftButtons.Controls.Add(SmallButton(Tr.T("Exporter…"), ExportProfile));

        var left = new GroupBox { Text = Tr.T("Profils  (•  = utilisé dans le patch)"), Dock = DockStyle.Left, Width = 330, Padding = new Padding(8) };
        left.Controls.Add(_list);
        left.Controls.Add(leftButtons);

        // ---------------- Profile details (right) ----------------
        var fields = Ui.Table(2);
        Ui.Row(fields, Tr.T("Nom"), _name);
        Ui.Row(fields, Tr.T("Fabricant"), _manufacturer);
        Ui.Row(fields, Tr.T("Notes"), _notes);

        var typeColumn = new DataGridViewComboBoxColumn
        {
            HeaderText = Tr.T("Rôle (type)"),
            FillWeight = 150,
            DataSource = ChannelTypeInfo.All.Select(t => new TypeItem(t.ToString(), ChannelTypeInfo.DisplayName(t))).ToList(),
            ValueMember = nameof(TypeItem.Value),
            DisplayMember = nameof(TypeItem.Text),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        };
        _grid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "N°", FillWeight = 30, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Nom du canal"), FillWeight = 140 },
            typeColumn,
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Défaut"), FillWeight = 45 },
            new DataGridViewCheckBoxColumn { HeaderText = Tr.T("Vérifié"), FillWeight = 45 },
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Presets (Nom=valeur; …)"), FillWeight = 200 },
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Notes"), FillWeight = 150 });
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
        channelButtons.Controls.Add(SmallButton(Tr.T("Ajouter un canal"), () => InsertChannel(atEnd: true)));
        channelButtons.Controls.Add(SmallButton(Tr.T("Insérer avant"), () => InsertChannel(atEnd: false)));
        channelButtons.Controls.Add(SmallButton(Tr.T("Supprimer le canal"), RemoveChannel));
        channelButtons.Controls.Add(SmallButton(Tr.T("Monter"), () => MoveChannel(-1)));
        channelButtons.Controls.Add(SmallButton(Tr.T("Descendre"), () => MoveChannel(+1)));
        channelButtons.Controls.Add(SmallButton(Tr.T("Tout marquer vérifié"), MarkAllVerified));
        channelButtons.Controls.Add(new Label
        {
            Text = Tr.T("L'ordre des lignes = l'ordre des canaux DMX de l'appareil. Changer le nombre de canaux décale les adresses : vérifie le patch ensuite."),
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(8, 6, 3, 3),
        });

        var right = new GroupBox { Text = Tr.T("Profil sélectionné"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        right.Controls.Add(_grid);
        right.Controls.Add(channelButtons);
        right.Controls.Add(fields);

        // ---------------- OK / Cancel ----------------
        var ok = new Button { Text = Tr.T("OK"), Width = 90, Height = 30 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = Tr.T("Annuler"), Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
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

    /// <summary>Small button running <paramref name="action"/>.</summary>
    private static Button SmallButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(2) };
        button.Click += (_, _) => action();
        return button;
    }

    // =================================================================================
    //  Selecting / loading / validating a profile
    // =================================================================================

    /// <summary>Another profile selected: the current one is validated first (stays on it on error).</summary>
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

        // The current profile is validated before moving to the next one; on error we stay on it.
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

    /// <summary>Shows the fields and channels of <paramref name="profile"/> (null = nothing selected).</summary>
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

    /// <summary>Adds a grid row for <paramref name="channel"/> (at the end, or at <paramref name="insertAt"/>).</summary>
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
        row.Tag = channel; // keeps the settings without a column (ranges, hardware strobe) for CommitCurrent
        row.Cells[ColName].Value = channel.Name;
        row.Cells[ColType].Value = channel.Type.ToString();
        row.Cells[ColDefault].Value = channel.Default.ToString();
        row.Cells[ColVerified].Value = channel.Verified;
        row.Cells[ColPresets].Value = ChannelPreset.Format(channel.Presets);
        row.Cells[ColNotes].Value = channel.Notes;
    }

    /// <summary>Rewrites the channel numbers of the rows.</summary>
    private void RenumberRows()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Cells[ColNum].Value = (row.Index + 1).ToString();
        }
    }

    /// <summary>Copies the editor content into the current profile. false (+ message) when invalid.</summary>
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
                errors.Add(Tr.F("Canal {0} : valeur par défaut « {1} » invalide (0 à 255).", number, defaultText));
                defaultValue = 0;
            }

            List<ChannelPreset> presets;
            try
            {
                presets = ChannelPreset.Parse(Convert.ToString(row.Cells[ColPresets].Value));
            }
            catch (FormatException ex)
            {
                errors.Add(Tr.F("Canal {0} : {1}", number, ex.Message));
                presets = new();
            }

            var original = row.Tag as ChannelDef; // null for a row added in the editor
            channels.Add(new ChannelDef
            {
                Name = name.Length > 0 ? name : Tr.F("Canal {0}", number),
                Type = type,
                Default = (byte)defaultValue,
                Verified = row.Cells[ColVerified].Value is true,
                Presets = presets,
                Notes = (Convert.ToString(row.Cells[ColNotes].Value) ?? "").Trim(),
                StrobeSlow = original?.StrobeSlow,
                StrobeFast = original?.StrobeFast,
                Ranges = original?.Ranges ?? new(),
            });
        }

        if (channels.Count == 0)
        {
            errors.Add(Tr.T("Un profil doit avoir au moins un canal."));
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, Tr.F("Le profil « {0} » contient des erreurs :\n\n", _name.Text) + string.Join("\n", errors),
                Tr.T("Profil invalide"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _current.Name = string.IsNullOrWhiteSpace(_name.Text) ? Tr.T("Module sans nom") : _name.Text.Trim();
        _current.Manufacturer = _manufacturer.Text.Trim();
        _current.Notes = _notes.Text.Trim();
        _current.Channels = channels;
        return true;
    }

    /// <summary>Forces the ListBox to redraw the text of its items (names changed).</summary>
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

    /// <summary>Adds a profile to the list and selects it.</summary>
    private void AddAndSelect(FixtureProfile profile)
    {
        _profiles.Add(profile);
        _list.Items.Add(profile);
        _list.SelectedItem = profile; // -> OnProfileSelected (the current profile is validated first)
    }

    // =================================================================================
    //  Profile actions
    // =================================================================================

    /// <summary>New profile: asks its name and channel count, creates generic channels.</summary>
    private void NewProfile()
    {
        if (!CommitCurrent())
        {
            return;
        }

        string? name = InputDialog.Ask(this, Tr.T("Nouveau profil"), Tr.T("Nom du nouveau module (ex. « Par LED RGBW 8 canaux ») :"));
        if (name is null)
        {
            return;
        }
        string? countText = InputDialog.Ask(this, Tr.T("Nouveau profil"),
            Tr.T("Nombre de canaux DMX (voir la notice de l'appareil, dans le mode DMX choisi) :"), "8");
        if (countText is null || !int.TryParse(countText, out int count) || count is < 1 or > 512)
        {
            MessageBox.Show(this, Tr.T("Nombre de canaux invalide (1 à 512)."), Tr.T("Nouveau profil"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var profile = new FixtureProfile { Name = name };
        for (int i = 1; i <= count; i++)
        {
            profile.Channels.Add(new ChannelDef { Name = Tr.F("Canal {0}", i) });
        }
        AddAndSelect(profile);
    }

    /// <summary>Duplicates the selected profile (new id, " (copy)" name).</summary>
    private void DuplicateProfile()
    {
        if (_current is null || !CommitCurrent())
        {
            return;
        }
        var copy = ShowJson.Clone(_current);
        copy.Id = FixtureProfile.NewId();
        copy.Name += Tr.T(" (copie)");
        AddAndSelect(copy);
    }

    /// <summary>Deletes the selected profile, unless fixtures of the patch use it.</summary>
    private void DeleteProfile()
    {
        if (_current is null)
        {
            return;
        }
        if (_usage.TryGetValue(_current.Id, out var users))
        {
            MessageBox.Show(this, Tr.F("Ce profil est utilisé par : {0}.\n", (string.Join(", ", users))) +
                                  Tr.T("Change ou supprime d'abord ces appareils dans le patch."),
                Tr.T("Profil utilisé"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Ui.Confirm(this, Tr.F("Supprimer le profil « {0} » ?", _current.Name)))
        {
            return;
        }

        var doomed = _current;
        _current = null; // no validation of the deleted profile
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

    /// <summary>Imports a *.dmxprofile.json file as a new profile.</summary>
    private void ImportProfile()
    {
        if (!CommitCurrent())
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = Tr.T("Importer un profil"),
            Filter = Tr.F("Profil d'appareil (*{0})|*{1}|Fichiers JSON (*.json)|*.json", ShowStore.ProfileExtension, ShowStore.ProfileExtension),
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
                profile.Name += Tr.T(" (importé)");
            }
            AddAndSelect(profile);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Tr.F("Impossible de lire ce profil :\n{0}", ex.Message), Tr.T("Import"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Exports the selected profile to a *.dmxprofile.json file.</summary>
    private void ExportProfile()
    {
        if (_current is null || !CommitCurrent())
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = Tr.T("Exporter le profil"),
            Filter = Tr.F("Profil d'appareil (*{0})|*{1}", ShowStore.ProfileExtension, ShowStore.ProfileExtension),
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
            MessageBox.Show(this, Tr.F("Impossible d'enregistrer :\n{0}", ex.Message), Tr.T("Export"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>File name without the characters Windows refuses ("sans-nom" when nothing is left).</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Trim().Length == 0 ? "sans-nom" : clean.Trim();
    }

    // =================================================================================
    //  Channel actions
    // =================================================================================

    /// <summary>Adds a channel at the end or before the selected row.</summary>
    private void InsertChannel(bool atEnd)
    {
        if (_current is null)
        {
            return;
        }
        int? at = atEnd || _grid.CurrentRow is null ? null : _grid.CurrentRow.Index;
        AddChannelRow(new ChannelDef { Name = Tr.F("Canal {0}", (_grid.Rows.Count + 1)) }, at);
        RenumberRows();
    }

    /// <summary>Deletes the selected channel row.</summary>
    private void RemoveChannel()
    {
        if (_grid.CurrentRow is DataGridViewRow row && _grid.Rows.Count > 1)
        {
            _grid.Rows.Remove(row);
            RenumberRows();
        }
    }

    /// <summary>Moves the selected channel up (-1) or down (+1).</summary>
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

    /// <summary>Ticks "Checked" on every channel of the profile.</summary>
    private void MarkAllVerified()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Cells[ColVerified].Value = true;
        }
    }

    /// <summary>OK: validates the current profile, then closes with the edited profiles.</summary>
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
