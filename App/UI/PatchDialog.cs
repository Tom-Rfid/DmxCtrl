// =====================================================================================
//  PatchDialog.cs
// -------------------------------------------------------------------------------------
//  "Patch / addressing" window: the list of connected fixtures (one fixture = one
//  tab in the main window).
//
//    - on the left, the list: Enabled, name, model, address, channels, Flash/strobe;
//      the Enabled and Flash boxes are ticked directly in the list;
//    - on the right, the card of the selected fixture: name, MODEL, address...
//      Picking a model (including a model of the built-in library: 260X moving head,
//      Spider, Ayra fog, Showtec laser, UV PAR) loads its channel count and suggests
//      a free address: the current one if it still fits, otherwise the first free
//      range that is large enough (between two fixtures or at the end).
//    - "Add": pick the model, the free address is found automatically.
//
//  A DISABLED fixture keeps its place in the patch but sends nothing and raises no
//  addressing warning (handy for a fixture missing tonight).
//  The dialog works on a COPY: nothing changes until OK is clicked.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;
using DmxController.Core.Storage;

namespace DmxController.UI;

/// <summary>"Patch / addressing" window: list of fixtures, their model and DMX address.</summary>
[DesignerCategory("Code")]
internal sealed class PatchDialog : Form
{
    /// <summary>Item of the model list: profile of the configuration or model of the library.</summary>
    private sealed record ModelItem(string Id, string Name, bool FromLibrary)
    {
        /// <summary>Text of the model list: translated name, "[library]" for a model not yet in the configuration.</summary>
        public override string ToString() => FromLibrary ? Tr.F("{0}   [bibliothèque]", Tr.T(Name)) : Tr.T(Name);
    }

    private const int ColEnabled = 0, ColName = 1, ColModel = 2, ColAddress = 3, ColCount = 4, ColEnd = 5, ColFlash = 6;

    private readonly List<FixtureInstance> _fixtures;
    private readonly List<FixtureProfile> _profiles;

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
    };
    private readonly Label _problems = new() { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(8) };

    // Card of the selected fixture
    private readonly TextBox _name = new() { Width = 250 };
    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290, DropDownWidth = 420 };
    private readonly NumericUpDown _address = new() { Minimum = 1, Maximum = 512, Width = 70 };
    private readonly Label _range = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly CheckBox _enabled = new() { Text = Tr.T("Actif (désactivé = n'envoie rien, aucune alerte d'adresse)"), AutoSize = true };
    private readonly CheckBox _flash = new() { Text = Tr.T("Flash / strobe (interrupteurs dans l'onglet Soirée)"), AutoSize = true };
    private readonly CheckBox _invertPan = new() { Text = Tr.T("Inverser pan"), AutoSize = true };
    private readonly CheckBox _invertTilt = new() { Text = Tr.T("Inverser tilt"), AutoSize = true };
    private readonly Label _freeRanges = new() { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 8, 3, 3) };
    private readonly GroupBox _detail = new() { Text = Tr.T("Appareil sélectionné"), Dock = DockStyle.Top, Height = 330, Padding = new Padding(8), Font = Ui.BoldFont };

    // Adding
    private readonly ComboBox _addModel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290, DropDownWidth = 420 };

    private bool _loading;

    /// <summary>Edited patch (valid only when DialogResult == OK).</summary>
    public List<FixtureInstance> ResultFixtures { get; private set; } = new();

    /// <summary>Profiles of the configuration + library models added while editing.</summary>
    public List<FixtureProfile> ResultProfiles { get; private set; } = new();

    /// <summary>Opens the window on a copy of the patch; <paramref name="addNewOnOpen"/> starts with the "add a fixture" choice.</summary>
    public PatchDialog(ShowConfig show, bool addNewOnOpen)
    {
        _fixtures = show.Fixtures.Select(f => ShowJson.Clone(f)).ToList();
        _profiles = show.Profiles.ToList(); // same objects: the profiles are not modified here

        Text = Tr.T("Patch — appareils et adressage DMX");
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1180, 640);
        MinimumSize = new Size(980, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        BuildGrid();
        var side = BuildSidePanel();

        var ok = new Button { Text = Tr.T("OK"), Width = 90, Height = 30 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = Tr.T("Annuler"), Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        bottom.Controls.AddRange(new Control[] { cancel, ok });
        CancelButton = cancel;

        Controls.Add(_grid);
        Controls.Add(side);
        Controls.Add(_problems);
        Controls.Add(bottom);

        FillModelCombos();
        RebuildGrid(selectIndex: 0);

        if (addNewOnOpen)
        {
            Shown += (_, _) =>
            {
                _addModel.Focus();
                _addModel.DroppedDown = true;
            };
        }
    }

    // =================================================================================
    //  Building
    // =================================================================================

    /// <summary>Fixture list (Enabled, name, model, address, channels, Flash/strobe).</summary>
    private void BuildGrid()
    {
        _grid.Columns.AddRange(
            new DataGridViewCheckBoxColumn { HeaderText = Tr.T("Actif"), FillWeight = 40 },
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Nom (titre de l'onglet)"), FillWeight = 130 },
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Modèle"), FillWeight = 230 },
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Adresse"), FillWeight = 55 },
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Canaux"), FillWeight = 50 },
            new DataGridViewTextBoxColumn { HeaderText = Tr.T("Dernier canal"), FillWeight = 60 },
            new DataGridViewCheckBoxColumn { HeaderText = Tr.T("Flash / strobe"), FillWeight = 55 });

        // Boxes are ticked with one click in the list (the list is read-only).
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _fixtures.Count)
            {
                return;
            }
            var fixture = _fixtures[e.RowIndex];
            if (e.ColumnIndex == ColEnabled)
            {
                fixture.Enabled = !fixture.Enabled;
            }
            else if (e.ColumnIndex == ColFlash)
            {
                fixture.Flash = !fixture.Flash;
            }
            else
            {
                return;
            }
            RefreshRow(e.RowIndex);
            LoadDetail();
            ValidateAll();
        };
        _grid.SelectionChanged += (_, _) => LoadDetail();
    }

    /// <summary>Right panel: card of the selected fixture and the "add a fixture" area.</summary>
    private Control BuildSidePanel()
    {
        var side = new Panel { Dock = DockStyle.Right, Width = 370, Padding = new Padding(6) };

        // ---------------- Card ----------------
        var table = Ui.Table(2);
        table.Font = new Font("Segoe UI", 9f);
        Ui.Row(table, Tr.T("Nom"), _name);
        Ui.Row(table, Tr.T("Modèle"), _model);
        var nextFree = new Button { Text = Tr.T("Adresse libre"), AutoSize = true };
        nextFree.Click += (_, _) => AssignNextFreeAddress();
        Ui.Row(table, Tr.T("Adresse DMX"), Ui.Flow(0, _address, nextFree));
        Ui.Row(table, "", _range);
        Ui.Row(table, "", _enabled);
        Ui.Row(table, "", _flash);
        Ui.Row(table, "", Ui.Flow(0, _invertPan, _invertTilt));
        Ui.Row(table, "", _freeRanges);
        _detail.Controls.Add(table);
        table.Dock = DockStyle.Fill;

        _name.TextChanged += (_, _) => EditSelected(f => f.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : Tr.T("Appareil"));
        _model.SelectionChangeCommitted += (_, _) => OnModelChosen();
        _address.ValueChanged += (_, _) => EditSelected(f => f.Address = (int)_address.Value);
        _enabled.CheckedChanged += (_, _) => EditSelected(f => f.Enabled = _enabled.Checked);
        _flash.CheckedChanged += (_, _) => EditSelected(f => f.Flash = _flash.Checked);
        _invertPan.CheckedChanged += (_, _) => EditSelected(f => f.InvertPan = _invertPan.Checked);
        _invertTilt.CheckedChanged += (_, _) => EditSelected(f => f.InvertTilt = _invertTilt.Checked);

        // ---------------- Add / list ----------------
        var addButton = new Button { Text = Tr.T("Ajouter cet appareil"), AutoSize = true, Font = Ui.BoldFont };
        addButton.Click += (_, _) => AddFixture();
        var duplicate = new Button { Text = Tr.T("Dupliquer"), Width = 100 };
        duplicate.Click += (_, _) => DuplicateFixture();
        var remove = new Button { Text = Tr.T("Supprimer"), Width = 100 };
        remove.Click += (_, _) => RemoveFixture();
        var up = new Button { Text = Tr.T("Monter"), Width = 100 };
        up.Click += (_, _) => MoveSelected(-1);
        var down = new Button { Text = Tr.T("Descendre"), Width = 100 };
        down.Click += (_, _) => MoveSelected(+1);

        var add = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        add.Controls.Add(Ui.Text(Tr.T("Ajouter un appareil — choisis son modèle :"), bold: true));
        add.Controls.Add(_addModel);
        add.Controls.Add(addButton);
        add.Controls.Add(Ui.Flow(0, duplicate, remove));
        add.Controls.Add(Ui.Flow(0, up, down));
        add.Controls.Add(new Label
        {
            Text = Tr.T("L'ordre des lignes = l'ordre des onglets.\nL'adresse doit être celle réglée dans le menu de l'appareil ") +
                   Tr.T("(ou ses micro-interrupteurs).\nPlusieurs appareils peuvent avoir le même modèle : chacun a son adresse et son onglet.\n") +
                   Tr.T("Un modèle absent de la liste : Appareils > Éditeur de profils."),
            AutoSize = true,
            MaximumSize = new Size(345, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 10, 3, 3),
        });

        side.Controls.Add(add);
        side.Controls.Add(_detail);
        return side;
    }

    /// <summary>Profiles of the configuration + library models not in it yet, in alphabetical order.</summary>
    private void FillModelCombos()
    {
        // Alphabetical order (ignoring case and accents).
        var items = _profiles.Select(p => new ModelItem(p.Id, p.Name, FromLibrary: false))
            .Concat(FixtureLibrary.All().Where(m => _profiles.All(p => p.Id != m.Id)).Select(m => new ModelItem(m.Id, m.Name, FromLibrary: true)))
            .OrderBy(m => TextKey.Normalize(m.Name), StringComparer.Ordinal)
            .ToArray();

        string? selectedAdd = (_addModel.SelectedItem as ModelItem)?.Id;
        _model.Items.Clear();
        _model.Items.AddRange(items);
        _addModel.Items.Clear();
        _addModel.Items.AddRange(items);
        _addModel.SelectedItem = items.FirstOrDefault(i => i.Id == selectedAdd) ?? items.FirstOrDefault();
    }

    // =================================================================================
    //  List
    // =================================================================================

    /// <summary>Profile of a fixture among the profiles being edited.</summary>
    private FixtureProfile? ProfileOf(FixtureInstance fixture) => _profiles.FirstOrDefault(p => p.Id == fixture.ProfileId);

    private int SelectedIndex => _grid.CurrentRow?.Index ?? -1;

    private FixtureInstance? Selected => SelectedIndex >= 0 && SelectedIndex < _fixtures.Count ? _fixtures[SelectedIndex] : null;

    /// <summary>Refills the list and selects row <paramref name="selectIndex"/>.</summary>
    private void RebuildGrid(int selectIndex)
    {
        _loading = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var _ in _fixtures)
            {
                _grid.Rows.Add();
            }
            for (int i = 0; i < _fixtures.Count; i++)
            {
                RefreshRow(i);
            }
            if (_fixtures.Count > 0)
            {
                int index = Math.Clamp(selectIndex, 0, _fixtures.Count - 1);
                _grid.CurrentCell = _grid.Rows[index].Cells[ColName];
            }
        }
        finally
        {
            _loading = false;
        }
        LoadDetail();
        ValidateAll();
    }

    /// <summary>Refreshes the cells of one row from its fixture.</summary>
    private void RefreshRow(int index)
    {
        var fixture = _fixtures[index];
        var profile = ProfileOf(fixture);
        var row = _grid.Rows[index];
        row.Cells[ColEnabled].Value = fixture.Enabled;
        row.Cells[ColName].Value = fixture.Name;
        row.Cells[ColModel].Value = profile?.Name ?? Tr.T("(profil introuvable)");
        row.Cells[ColAddress].Value = fixture.Address;
        row.Cells[ColCount].Value = profile?.ChannelCount.ToString() ?? "?";
        row.Cells[ColEnd].Value = profile is null ? "?" : fixture.EndAddress(profile).ToString();
        row.Cells[ColFlash].Value = fixture.Flash;
        row.DefaultCellStyle.ForeColor = fixture.Enabled ? SystemColors.ControlText : Color.Gray;
    }

    /// <summary>Checks the whole patch, shows the problems and the free channels.</summary>
    private List<PatchProblem> ValidateAll()
    {
        var problems = PatchValidator.Validate(_fixtures, _profiles);
        var badIds = problems.SelectMany(p => p.FixtureIds).ToHashSet();
        for (int i = 0; i < _fixtures.Count; i++)
        {
            _grid.Rows[i].DefaultCellStyle.BackColor = badIds.Contains(_fixtures[i].Id) ? Color.MistyRose : Color.Empty;
        }

        if (problems.Count == 0)
        {
            _problems.ForeColor = Ui.OkColor;
            int disabled = _fixtures.Count(f => !f.Enabled);
            _problems.Text = Tr.T("Aucun conflit d'adressage.") + (disabled > 0 ? Tr.F("  ({0} appareil(s) désactivé(s), ignoré(s))", disabled) : "");
        }
        else
        {
            _problems.ForeColor = Color.DarkRed;
            _problems.Text = string.Join(Environment.NewLine, problems.Select(p => "• " + p.Message));
        }

        var free = PatchValidator.FreeRanges(_fixtures, _profiles);
        _freeRanges.Text = Tr.T("Canaux libres : ") + (free.Count == 0 ? Tr.T("aucun (univers plein)") : string.Join(", ", free));
        return problems;
    }

    // =================================================================================
    //  Card of the selected fixture
    // =================================================================================

    /// <summary>Loads the card of the selected fixture.</summary>
    private void LoadDetail()
    {
        if (_loading)
        {
            return;
        }

        var fixture = Selected;
        _detail.Enabled = fixture is not null;
        if (fixture is null)
        {
            _range.Text = "";
            return;
        }

        _loading = true;
        try
        {
            _detail.Text = Tr.F("Appareil sélectionné : {0}", fixture.Name);
            _name.Text = fixture.Name;
            _model.SelectedItem = _model.Items.Cast<ModelItem>().FirstOrDefault(m => m.Id == fixture.ProfileId);
            _address.Value = Math.Clamp(fixture.Address, 1, 512);
            _enabled.Checked = fixture.Enabled;
            _flash.Checked = fixture.Flash;
            _invertPan.Checked = fixture.InvertPan;
            _invertTilt.Checked = fixture.InvertTilt;

            var profile = ProfileOf(fixture);
            bool canMove = profile?.CanMove == true;
            _invertPan.Enabled = _invertTilt.Enabled = canMove;
            UpdateRangeLabel(fixture);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Text under the address: channel range and whether it is free.</summary>
    private void UpdateRangeLabel(FixtureInstance fixture)
    {
        var profile = ProfileOf(fixture);
        if (profile is null)
        {
            _range.Text = Tr.T("Choisis un modèle.");
            _range.ForeColor = Color.DarkRed;
            return;
        }

        int end = fixture.EndAddress(profile);
        bool free = PatchValidator.IsFree(_fixtures, _profiles, fixture.Address, profile.ChannelCount, fixture.Id);
        _range.Text = Tr.F("{0} canaux : {1} à {2}", profile.ChannelCount, fixture.Address, end) +
                      (!fixture.Enabled ? Tr.T("  (désactivé)") : free ? Tr.T("  — libre") : Tr.T("  — OCCUPÉ ou hors univers"));
        _range.ForeColor = !fixture.Enabled ? Color.Gray : free ? Ui.OkColor : Color.DarkRed;
    }

    /// <summary>Applies a change of the card to the selected fixture, then refreshes.</summary>
    private void EditSelected(Action<FixtureInstance> edit)
    {
        if (_loading || Selected is not FixtureInstance fixture)
        {
            return;
        }
        edit(fixture);
        RefreshRow(SelectedIndex);
        UpdateRangeLabel(fixture);
        _detail.Text = Tr.F("Appareil sélectionné : {0}", fixture.Name);
        ValidateAll();
    }

    /// <summary>
    /// New model for the fixture: its profile is added to the configuration when it comes from
    /// the library, the channel count follows, the address is kept if it still fits, otherwise
    /// the first free range that is large enough is suggested. The name follows the model when
    /// it was the default name of the old model ("Spider" -> "Lyre 3").
    /// </summary>
    private void OnModelChosen()
    {
        if (_loading || Selected is not FixtureInstance fixture || _model.SelectedItem is not ModelItem item)
        {
            return;
        }

        var oldProfile = ProfileOf(fixture);
        var profile = FixtureLibrary.EnsureProfile(_profiles, item.Id);
        if (profile is null || profile.Id == fixture.ProfileId)
        {
            return;
        }

        fixture.ProfileId = profile.Id;
        if (oldProfile is not null && IsDefaultName(fixture.Name, oldProfile.FixtureNameBase))
        {
            fixture.Name = UniqueName(profile.FixtureNameBase, fixture.Id);
        }
        fixture.Flash = !profile.IsFogMachine; // the fog machine has no flash switch

        int address = PatchValidator.SuggestAddress(_fixtures, _profiles, profile.ChannelCount, fixture.Address, fixture.Id);
        string message;
        if (address < 0)
        {
            message = Tr.F("Plus de plage libre de {0} canaux : adresse {1} conservée (conflit).", profile.ChannelCount, fixture.Address);
        }
        else if (address != fixture.Address)
        {
            message = Tr.F("Adresse {0} occupée pour {1} canaux : {2} proposée.", fixture.Address, profile.ChannelCount, address);
            fixture.Address = address;
        }
        else
        {
            message = Tr.F("Adresse {0} conservée ({1} canaux libres).", address, profile.ChannelCount);
        }

        int index = SelectedIndex;
        FillModelCombos();
        RefreshRow(index);
        LoadDetail();
        ValidateAll();
        _range.Text += Environment.NewLine + message;
    }

    /// <summary>true when <paramref name="name"/> is a default name of the model ("Lyre", "Lyre 2"...) or the generic "Appareil" / "Fixture".</summary>
    private static bool IsDefaultName(string name, string baseName)
    {
        string trimmed = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ');
        return string.Equals(trimmed, baseName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Appareil", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, Tr.T("Appareil"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>First free name "base N" (N = 1, 2, 3...) among the other fixtures.</summary>
    private string UniqueName(string baseName, string? ignoreId = null)
    {
        var names = _fixtures.Where(f => f.Id != ignoreId).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // =================================================================================
    //  Actions
    // =================================================================================

    /// <summary>Adds a fixture of the chosen model at the first free address (disabled when the universe is full).</summary>
    private void AddFixture()
    {
        if (_addModel.SelectedItem is not ModelItem item || FixtureLibrary.EnsureProfile(_profiles, item.Id) is not FixtureProfile profile)
        {
            MessageBox.Show(this, Tr.T("Choisis d'abord le modèle de l'appareil dans la liste."), Tr.T("Modèle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int address = PatchValidator.NextFreeAddress(_fixtures, _profiles, profile.ChannelCount);
        var fixture = new FixtureInstance
        {
            Name = UniqueName(profile.FixtureNameBase),
            ProfileId = profile.Id,
            Address = address > 0 ? address : 1,
            Flash = !profile.IsFogMachine,
            Enabled = address > 0,
        };
        _fixtures.Add(fixture);
        FillModelCombos();
        RebuildGrid(_fixtures.Count - 1);

        if (address < 0)
        {
            MessageBox.Show(this, Tr.F("Plus aucune plage libre de {0} canaux dans l'univers DMX.\n", profile.ChannelCount) +
                                  Tr.T("L'appareil est ajouté DÉSACTIVÉ : libère de la place ou désactive un autre appareil."),
                Tr.T("Univers plein"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _name.Focus();
        _name.SelectAll();
    }

    /// <summary>Duplicates the selected fixture at the next free address.</summary>
    private void DuplicateFixture()
    {
        if (Selected is not FixtureInstance original)
        {
            return;
        }
        var profile = ProfileOf(original);
        var copy = ShowJson.Clone(original);
        copy.Id = FixtureProfile.NewId();
        copy.Name = UniqueName(original.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' '));
        int address = PatchValidator.NextFreeAddress(_fixtures, _profiles, profile?.ChannelCount ?? 1);
        copy.Address = address > 0 ? address : original.Address;
        _fixtures.Insert(SelectedIndex + 1, copy);
        RebuildGrid(SelectedIndex + 1);
    }

    /// <summary>Removes the selected fixture.</summary>
    private void RemoveFixture()
    {
        if (Selected is not FixtureInstance fixture)
        {
            return;
        }
        int index = SelectedIndex;
        _fixtures.Remove(fixture);
        RebuildGrid(index);
    }

    /// <summary>"Free address" button: moves the selected fixture to the first free range.</summary>
    private void AssignNextFreeAddress()
    {
        if (Selected is not FixtureInstance fixture)
        {
            return;
        }
        var profile = ProfileOf(fixture);
        int address = PatchValidator.NextFreeAddress(_fixtures, _profiles, profile?.ChannelCount ?? 1, fixture.Id);
        if (address < 0)
        {
            MessageBox.Show(this, Tr.T("Plus aucune plage libre dans l'univers DMX."), Tr.T("Univers plein"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _address.Value = address; // -> EditSelected
    }

    /// <summary>Moves the selected fixture up (-1) or down (+1): the order of the rows is the order of the tabs.</summary>
    private void MoveSelected(int delta)
    {
        int index = SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _fixtures.Count)
        {
            return;
        }
        (_fixtures[index], _fixtures[target]) = (_fixtures[target], _fixtures[index]);
        RebuildGrid(target);
    }

    /// <summary>OK: warns about remaining conflicts, then closes with the edited patch.</summary>
    private void Accept()
    {
        var problems = ValidateAll();
        if (problems.Count > 0 &&
            !Ui.Confirm(this, Tr.T("Il reste des problèmes d'adressage :\n\n") + string.Join("\n", problems.Select(p => "• " + p.Message)) +
                              Tr.T("\n\nValider quand même ? (tu peux aussi désactiver un appareil absent ce soir)"), Tr.T("Conflits d'adressage")))
        {
            return;
        }

        ResultFixtures = _fixtures;
        ResultProfiles = _profiles;
        DialogResult = DialogResult.OK;
        Close();
    }
}
