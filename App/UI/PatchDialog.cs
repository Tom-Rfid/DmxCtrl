// =====================================================================================
//  PatchDialog.cs
// -------------------------------------------------------------------------------------
//  Fenetre "Patch / adressage" : la liste des appareils branches (un appareil = un
//  onglet dans la fenetre principale).
//
//    - a gauche, la liste : Actif, nom, modele, adresse, canaux, Flash/strobe ;
//      les cases Actif et Flash se cochent directement dans la liste ;
//    - a droite, la fiche de l'appareil selectionne : nom, MODELE, adresse...
//      Choisir un modele (y compris un modele de la bibliotheque integree : lyre 260X,
//      Spider, fumee Ayra, laser Showtec, PAR UV) charge son nombre de canaux et
//      propose une adresse libre : l'adresse actuelle si elle tient encore, sinon la
//      premiere plage libre assez grande (entre deux appareils ou a la fin).
//    - "Ajouter" : choisir le modele, l'adresse libre est trouvee toute seule.
//
//  Un appareil DESACTIVE garde sa place dans le patch mais n'envoie rien et ne
//  declenche aucune alerte d'adressage (pratique pour un appareil absent ce soir).
//  On travaille sur une COPIE : rien ne change tant qu'on n'a pas clique sur OK.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;
using DmxController.Core.Storage;

namespace DmxController.UI;

[DesignerCategory("Code")]
internal sealed class PatchDialog : Form
{
    /// <summary>Element de la liste des modeles : profil de la config ou modele de la bibliotheque.</summary>
    private sealed record ModelItem(string Id, string Name, bool FromLibrary)
    {
        public override string ToString() => FromLibrary ? $"{Name}   [bibliothèque]" : Name;
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

    // Fiche de l'appareil selectionne
    private readonly TextBox _name = new() { Width = 250 };
    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290, DropDownWidth = 420 };
    private readonly NumericUpDown _address = new() { Minimum = 1, Maximum = 512, Width = 70 };
    private readonly Label _range = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly CheckBox _enabled = new() { Text = "Actif (désactivé = n'envoie rien, aucune alerte d'adresse)", AutoSize = true };
    private readonly CheckBox _flash = new() { Text = "Flash / strobe (interrupteurs dans l'onglet Soirée)", AutoSize = true };
    private readonly CheckBox _invertPan = new() { Text = "Inverser pan", AutoSize = true };
    private readonly CheckBox _invertTilt = new() { Text = "Inverser tilt", AutoSize = true };
    private readonly Label _freeRanges = new() { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 8, 3, 3) };
    private readonly GroupBox _detail = new() { Text = "Appareil sélectionné", Dock = DockStyle.Top, Height = 330, Padding = new Padding(8), Font = Ui.BoldFont };

    // Ajout
    private readonly ComboBox _addModel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290, DropDownWidth = 420 };

    private bool _loading;

    public List<FixtureInstance> ResultFixtures { get; private set; } = new();

    /// <summary>Profils de la configuration + modeles de la bibliotheque ajoutes pendant l'edition.</summary>
    public List<FixtureProfile> ResultProfiles { get; private set; } = new();

    public PatchDialog(ShowConfig show, bool addNewOnOpen)
    {
        _fixtures = show.Fixtures.Select(f => ShowJson.Clone(f)).ToList();
        _profiles = show.Profiles.ToList(); // memes objets : les profils ne sont pas modifies ici

        Text = "Patch — appareils et adressage DMX";
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1180, 640);
        MinimumSize = new Size(980, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        BuildGrid();
        var side = BuildSidePanel();

        var ok = new Button { Text = "OK", Width = 90, Height = 30 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Annuler", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
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
    //  Construction
    // =================================================================================

    private void BuildGrid()
    {
        _grid.Columns.AddRange(
            new DataGridViewCheckBoxColumn { HeaderText = "Actif", FillWeight = 40 },
            new DataGridViewTextBoxColumn { HeaderText = "Nom (titre de l'onglet)", FillWeight = 130 },
            new DataGridViewTextBoxColumn { HeaderText = "Modèle", FillWeight = 230 },
            new DataGridViewTextBoxColumn { HeaderText = "Adresse", FillWeight = 55 },
            new DataGridViewTextBoxColumn { HeaderText = "Canaux", FillWeight = 50 },
            new DataGridViewTextBoxColumn { HeaderText = "Dernier canal", FillWeight = 60 },
            new DataGridViewCheckBoxColumn { HeaderText = "Flash / strobe", FillWeight = 55 });

        // Les cases se cochent d'un clic dans la liste (la liste est en lecture seule).
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

    private Control BuildSidePanel()
    {
        var side = new Panel { Dock = DockStyle.Right, Width = 370, Padding = new Padding(6) };

        // ---------------- Fiche ----------------
        var table = Ui.Table(2);
        table.Font = new Font("Segoe UI", 9f);
        Ui.Row(table, "Nom", _name);
        Ui.Row(table, "Modèle", _model);
        var nextFree = new Button { Text = "Adresse libre", AutoSize = true };
        nextFree.Click += (_, _) => AssignNextFreeAddress();
        Ui.Row(table, "Adresse DMX", Ui.Flow(0, _address, nextFree));
        Ui.Row(table, "", _range);
        Ui.Row(table, "", _enabled);
        Ui.Row(table, "", _flash);
        Ui.Row(table, "", Ui.Flow(0, _invertPan, _invertTilt));
        Ui.Row(table, "", _freeRanges);
        _detail.Controls.Add(table);
        table.Dock = DockStyle.Fill;

        _name.TextChanged += (_, _) => EditSelected(f => f.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : "Appareil");
        _model.SelectionChangeCommitted += (_, _) => OnModelChosen();
        _address.ValueChanged += (_, _) => EditSelected(f => f.Address = (int)_address.Value);
        _enabled.CheckedChanged += (_, _) => EditSelected(f => f.Enabled = _enabled.Checked);
        _flash.CheckedChanged += (_, _) => EditSelected(f => f.Flash = _flash.Checked);
        _invertPan.CheckedChanged += (_, _) => EditSelected(f => f.InvertPan = _invertPan.Checked);
        _invertTilt.CheckedChanged += (_, _) => EditSelected(f => f.InvertTilt = _invertTilt.Checked);

        // ---------------- Ajout / liste ----------------
        var addButton = new Button { Text = "Ajouter cet appareil", AutoSize = true, Font = Ui.BoldFont };
        addButton.Click += (_, _) => AddFixture();
        var duplicate = new Button { Text = "Dupliquer", Width = 100 };
        duplicate.Click += (_, _) => DuplicateFixture();
        var remove = new Button { Text = "Supprimer", Width = 100 };
        remove.Click += (_, _) => RemoveFixture();
        var up = new Button { Text = "Monter", Width = 100 };
        up.Click += (_, _) => MoveSelected(-1);
        var down = new Button { Text = "Descendre", Width = 100 };
        down.Click += (_, _) => MoveSelected(+1);

        var add = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        add.Controls.Add(Ui.Text("Ajouter un appareil — choisis son modèle :", bold: true));
        add.Controls.Add(_addModel);
        add.Controls.Add(addButton);
        add.Controls.Add(Ui.Flow(0, duplicate, remove));
        add.Controls.Add(Ui.Flow(0, up, down));
        add.Controls.Add(new Label
        {
            Text = "L'ordre des lignes = l'ordre des onglets.\nL'adresse doit être celle réglée dans le menu de l'appareil " +
                   "(ou ses micro-interrupteurs).\nPlusieurs appareils peuvent avoir le même modèle : chacun a son adresse et son onglet.\n" +
                   "Un modèle absent de la liste : Appareils > Éditeur de profils.",
            AutoSize = true,
            MaximumSize = new Size(345, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 10, 3, 3),
        });

        side.Controls.Add(add);
        side.Controls.Add(_detail);
        return side;
    }

    /// <summary>Profils de la config + modeles de la bibliotheque qui n'y sont pas encore, par ordre alphabetique.</summary>
    private void FillModelCombos()
    {
        // Ordre alphabetique (sans tenir compte des majuscules ni des accents).
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
    //  Liste
    // =================================================================================

    private FixtureProfile? ProfileOf(FixtureInstance fixture) => _profiles.FirstOrDefault(p => p.Id == fixture.ProfileId);

    private int SelectedIndex => _grid.CurrentRow?.Index ?? -1;

    private FixtureInstance? Selected => SelectedIndex >= 0 && SelectedIndex < _fixtures.Count ? _fixtures[SelectedIndex] : null;

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

    private void RefreshRow(int index)
    {
        var fixture = _fixtures[index];
        var profile = ProfileOf(fixture);
        var row = _grid.Rows[index];
        row.Cells[ColEnabled].Value = fixture.Enabled;
        row.Cells[ColName].Value = fixture.Name;
        row.Cells[ColModel].Value = profile?.Name ?? "(profil introuvable)";
        row.Cells[ColAddress].Value = fixture.Address;
        row.Cells[ColCount].Value = profile?.ChannelCount.ToString() ?? "?";
        row.Cells[ColEnd].Value = profile is null ? "?" : fixture.EndAddress(profile).ToString();
        row.Cells[ColFlash].Value = fixture.Flash;
        row.DefaultCellStyle.ForeColor = fixture.Enabled ? SystemColors.ControlText : Color.Gray;
    }

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
            _problems.Text = "Aucun conflit d'adressage." + (disabled > 0 ? $"  ({disabled} appareil(s) désactivé(s), ignoré(s))" : "");
        }
        else
        {
            _problems.ForeColor = Color.DarkRed;
            _problems.Text = string.Join(Environment.NewLine, problems.Select(p => "• " + p.Message));
        }

        var free = PatchValidator.FreeRanges(_fixtures, _profiles);
        _freeRanges.Text = "Canaux libres : " + (free.Count == 0 ? "aucun (univers plein)" : string.Join(", ", free));
        return problems;
    }

    // =================================================================================
    //  Fiche de l'appareil selectionne
    // =================================================================================

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
            _detail.Text = $"Appareil sélectionné : {fixture.Name}";
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

    private void UpdateRangeLabel(FixtureInstance fixture)
    {
        var profile = ProfileOf(fixture);
        if (profile is null)
        {
            _range.Text = "Choisis un modèle.";
            _range.ForeColor = Color.DarkRed;
            return;
        }

        int end = fixture.EndAddress(profile);
        bool free = PatchValidator.IsFree(_fixtures, _profiles, fixture.Address, profile.ChannelCount, fixture.Id);
        _range.Text = $"{profile.ChannelCount} canaux : {fixture.Address} à {end}" +
                      (!fixture.Enabled ? "  (désactivé)" : free ? "  — libre" : "  — OCCUPÉ ou hors univers");
        _range.ForeColor = !fixture.Enabled ? Color.Gray : free ? Ui.OkColor : Color.DarkRed;
    }

    /// <summary>Applique une modification de la fiche a l'appareil selectionne, puis rafraichit.</summary>
    private void EditSelected(Action<FixtureInstance> edit)
    {
        if (_loading || Selected is not FixtureInstance fixture)
        {
            return;
        }
        edit(fixture);
        RefreshRow(SelectedIndex);
        UpdateRangeLabel(fixture);
        _detail.Text = $"Appareil sélectionné : {fixture.Name}";
        ValidateAll();
    }

    /// <summary>
    /// Nouveau modele pour l'appareil : son profil est ajoute a la config si c'est un modele de la
    /// bibliotheque, le nombre de canaux suit, l'adresse est conservee si elle tient encore,
    /// sinon la premiere plage libre assez grande est proposee. Le nom suit le modele s'il
    /// etait le nom par defaut de l'ancien modele (« Spider » -> « Lyre 3 »).
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
        fixture.Flash = !profile.IsFogMachine; // la fumee n'a pas d'interrupteur flash

        int address = PatchValidator.SuggestAddress(_fixtures, _profiles, profile.ChannelCount, fixture.Address, fixture.Id);
        string message;
        if (address < 0)
        {
            message = $"Plus de plage libre de {profile.ChannelCount} canaux : adresse {fixture.Address} conservée (conflit).";
        }
        else if (address != fixture.Address)
        {
            message = $"Adresse {fixture.Address} occupée pour {profile.ChannelCount} canaux : {address} proposée.";
            fixture.Address = address;
        }
        else
        {
            message = $"Adresse {address} conservée ({profile.ChannelCount} canaux libres).";
        }

        int index = SelectedIndex;
        FillModelCombos();
        RefreshRow(index);
        LoadDetail();
        ValidateAll();
        _range.Text += Environment.NewLine + message;
    }

    private static bool IsDefaultName(string name, string baseName)
    {
        string trimmed = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ');
        return string.Equals(trimmed, baseName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Appareil", StringComparison.OrdinalIgnoreCase);
    }

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

    private void AddFixture()
    {
        if (_addModel.SelectedItem is not ModelItem item || FixtureLibrary.EnsureProfile(_profiles, item.Id) is not FixtureProfile profile)
        {
            MessageBox.Show(this, "Choisis d'abord le modèle de l'appareil dans la liste.", "Modèle", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            MessageBox.Show(this, $"Plus aucune plage libre de {profile.ChannelCount} canaux dans l'univers DMX.\n" +
                                  "L'appareil est ajouté DÉSACTIVÉ : libère de la place ou désactive un autre appareil.",
                "Univers plein", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _name.Focus();
        _name.SelectAll();
    }

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
            MessageBox.Show(this, "Plus aucune plage libre dans l'univers DMX.", "Univers plein", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _address.Value = address; // -> EditSelected
    }

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

    private void Accept()
    {
        var problems = ValidateAll();
        if (problems.Count > 0 &&
            !Ui.Confirm(this, "Il reste des problèmes d'adressage :\n\n" + string.Join("\n", problems.Select(p => "• " + p.Message)) +
                              "\n\nValider quand même ? (tu peux aussi désactiver un appareil absent ce soir)", "Conflits d'adressage"))
        {
            return;
        }

        ResultFixtures = _fixtures;
        ResultProfiles = _profiles;
        DialogResult = DialogResult.OK;
        Close();
    }
}
