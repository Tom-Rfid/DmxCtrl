// =====================================================================================
//  PatchDialog.cs
// -------------------------------------------------------------------------------------
//  Fenetre "Patch / adressage" : la liste des appareils branches.
//  Chaque ligne = un appareil = un onglet dans la fenetre principale.
//    - Nom (titre de l'onglet), profil (module), adresse DMX de depart
//    - Actif (un appareil desactive n'envoie rien mais garde sa config)
//    - Inverser pan / tilt (sens des effets de mouvement)
//  Les conflits (chevauchements, depassement du canal 512) sont signales en rouge
//  en temps reel. On travaille sur une COPIE : rien ne change tant qu'on n'a pas
//  clique sur OK.
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;

namespace DmxController.UI;

[DesignerCategory("Code")]
internal sealed class PatchDialog : Form
{
    private sealed record ProfileItem(string Id, string Name);

    private const int ColName = 0, ColProfile = 1, ColAddress = 2, ColCount = 3, ColEnd = 4, ColEnabled = 5, ColInvPan = 6, ColInvTilt = 7;

    private readonly IReadOnlyList<FixtureProfile> _profiles;

    // Reglages d'appareil sans colonne dans la grille (ailes en miroir, pas de strobe : regles
    // dans l'onglet de l'appareil) : on les conserve par Id pour ne pas les perdre en validant.
    private readonly Dictionary<string, (bool MirrorMotors, bool NoStrobe)> _hidden = new();

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
    private readonly Label _problems = new() { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(8) };

    public List<FixtureInstance> ResultFixtures { get; private set; } = new();

    public PatchDialog(ShowConfig show, bool addNewOnOpen)
    {
        _profiles = show.Profiles;
        foreach (var fixture in show.Fixtures)
        {
            _hidden[fixture.Id] = (fixture.MirrorMotors, fixture.NoStrobe);
        }
        Text = "Patch — appareils et adressage DMX";
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1040, 560);
        MinimumSize = new Size(820, 400);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var profileColumn = new DataGridViewComboBoxColumn
        {
            HeaderText = "Profil (module)",
            FillWeight = 230,
            DataSource = _profiles.Select(p => new ProfileItem(p.Id, p.Name)).ToList(),
            DisplayMember = nameof(ProfileItem.Name),
            ValueMember = nameof(ProfileItem.Id),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        };
        _grid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Nom (titre de l'onglet)", FillWeight = 150 },
            profileColumn,
            new DataGridViewTextBoxColumn { HeaderText = "Adresse DMX", FillWeight = 70 },
            new DataGridViewTextBoxColumn { HeaderText = "Canaux", FillWeight = 55, ReadOnly = true },
            new DataGridViewTextBoxColumn { HeaderText = "Dernier canal", FillWeight = 70, ReadOnly = true },
            new DataGridViewCheckBoxColumn { HeaderText = "Actif", FillWeight = 45 },
            new DataGridViewCheckBoxColumn { HeaderText = "Inverser pan", FillWeight = 60 },
            new DataGridViewCheckBoxColumn { HeaderText = "Inverser tilt", FillWeight = 60 });
        _grid.Columns[ColCount].DefaultCellStyle.BackColor = SystemColors.Control;
        _grid.Columns[ColEnd].DefaultCellStyle.BackColor = SystemColors.Control;

        foreach (var fixture in show.Fixtures)
        {
            AddRow(fixture);
        }

        // Les cases a cocher et listes doivent valider immediatement, pas a la sortie de la cellule.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                RecomputeRow(_grid.Rows[e.RowIndex]);
                ValidateRows();
            }
        };
        _grid.DataError += (_, e) => e.ThrowException = false;

        // ---------------- Boutons ----------------
        var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 200, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8) };
        side.Controls.Add(SideButton("Ajouter un appareil", AddFixture));
        side.Controls.Add(SideButton("Dupliquer", DuplicateFixture));
        side.Controls.Add(SideButton("Supprimer", RemoveFixture));
        side.Controls.Add(SideButton("Adresse libre suivante", AssignNextFreeAddress));
        side.Controls.Add(SideButton("Monter", () => MoveRow(-1)));
        side.Controls.Add(SideButton("Descendre", () => MoveRow(+1)));
        side.Controls.Add(new Label
        {
            Text = "L'ordre des lignes = l'ordre des onglets.\n\nL'adresse doit être la même que celle réglée dans le menu de l'appareil.\n\n" +
                   "Pour un nouveau TYPE d'appareil, crée d'abord son profil : Appareils > Éditeur de profils.",
            AutoSize = true,
            MaximumSize = new Size(180, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 12, 3, 3),
        });

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

        ValidateRows();
        if (addNewOnOpen)
        {
            Shown += (_, _) => AddFixture();
        }
    }

    private static Button SideButton(string text, Action action)
    {
        var button = new Button { Text = text, Width = 180, Height = 30, Margin = new Padding(3, 3, 3, 4) };
        button.Click += (_, _) => action();
        return button;
    }

    private DataGridViewRow AddRow(FixtureInstance fixture, int? insertAt = null)
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
        row.Tag = fixture.Id;
        row.Cells[ColName].Value = fixture.Name;
        // Une valeur absente de la liste ferait planter la cellule : on retombe sur le premier profil.
        row.Cells[ColProfile].Value = _profiles.Any(p => p.Id == fixture.ProfileId) ? fixture.ProfileId : _profiles.FirstOrDefault()?.Id;
        row.Cells[ColAddress].Value = fixture.Address.ToString();
        row.Cells[ColEnabled].Value = fixture.Enabled;
        row.Cells[ColInvPan].Value = fixture.InvertPan;
        row.Cells[ColInvTilt].Value = fixture.InvertTilt;
        RecomputeRow(row);
        return row;
    }

    private void RecomputeRow(DataGridViewRow row)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == row.Cells[ColProfile].Value as string);
        bool ok = int.TryParse(Convert.ToString(row.Cells[ColAddress].Value)?.Trim(), out int address);
        row.Cells[ColCount].Value = profile?.ChannelCount.ToString() ?? "?";
        row.Cells[ColEnd].Value = ok && profile is not null ? (address + profile.ChannelCount - 1).ToString() : "?";
    }

    private List<FixtureInstance> ReadRows()
    {
        _grid.EndEdit();
        var list = new List<FixtureInstance>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            int.TryParse(Convert.ToString(row.Cells[ColAddress].Value)?.Trim(), out int address);
            string name = (Convert.ToString(row.Cells[ColName].Value) ?? "").Trim();
            list.Add(new FixtureInstance
            {
                Id = (string)row.Tag!,
                Name = name.Length > 0 ? name : "Appareil",
                ProfileId = row.Cells[ColProfile].Value as string ?? "",
                Address = address,
                Enabled = row.Cells[ColEnabled].Value is true,
                InvertPan = row.Cells[ColInvPan].Value is true,
                InvertTilt = row.Cells[ColInvTilt].Value is true,
                MirrorMotors = _hidden.GetValueOrDefault((string)row.Tag!).MirrorMotors,
                NoStrobe = _hidden.GetValueOrDefault((string)row.Tag!).NoStrobe,
            });
        }
        return list;
    }

    private List<PatchProblem> ValidateRows()
    {
        var problems = PatchValidator.Validate(ReadRows(), _profiles);
        var badIds = problems.SelectMany(p => p.FixtureIds).ToHashSet();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.DefaultCellStyle.BackColor = badIds.Contains((string)row.Tag!) ? Color.MistyRose : Color.Empty;
        }

        if (problems.Count == 0)
        {
            _problems.ForeColor = Ui.OkColor;
            _problems.Text = "Aucun conflit d'adressage.";
        }
        else
        {
            _problems.ForeColor = Color.DarkRed;
            _problems.Text = string.Join(Environment.NewLine, problems.Select(p => "• " + p.Message));
        }
        return problems;
    }

    private FixtureProfile? ProfileOfRow(DataGridViewRow? row) =>
        _profiles.FirstOrDefault(p => p.Id == row?.Cells[ColProfile].Value as string) ?? _profiles.FirstOrDefault();

    private static string UniqueName(IEnumerable<FixtureInstance> fixtures, string baseName)
    {
        var names = fixtures.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void SelectRow(DataGridViewRow row, bool editName)
    {
        _grid.ClearSelection();
        row.Selected = true;
        _grid.CurrentCell = row.Cells[ColName];
        if (editName)
        {
            _grid.BeginEdit(selectAll: true);
        }
    }

    private void AddFixture()
    {
        var profile = ProfileOfRow(_grid.CurrentRow);
        if (profile is null)
        {
            MessageBox.Show(this, "Aucun profil disponible : crée d'abord un profil (Appareils > Éditeur de profils).",
                "Pas de profil", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var current = ReadRows();
        int address = PatchValidator.NextFreeAddress(current, _profiles, profile.ChannelCount);
        string baseName = profile.CanMove ? "Lyre" : "Appareil";
        var fixture = new FixtureInstance
        {
            Name = UniqueName(current, baseName),
            ProfileId = profile.Id,
            Address = address > 0 ? address : 1,
        };
        SelectRow(AddRow(fixture), editName: true);
        ValidateRows();
    }

    private void DuplicateFixture()
    {
        if (_grid.CurrentRow is not DataGridViewRow source)
        {
            return;
        }

        var current = ReadRows();
        var original = current[source.Index];
        var profile = ProfileOfRow(source);
        int address = PatchValidator.NextFreeAddress(current, _profiles, profile?.ChannelCount ?? 1);
        var copy = new FixtureInstance
        {
            Name = UniqueName(current, original.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ')),
            ProfileId = original.ProfileId,
            Address = address > 0 ? address : original.Address,
            InvertPan = original.InvertPan,
            InvertTilt = original.InvertTilt,
        };
        _hidden[copy.Id] = (original.MirrorMotors, original.NoStrobe);
        SelectRow(AddRow(copy, source.Index + 1), editName: true);
        ValidateRows();
    }

    private void RemoveFixture()
    {
        if (_grid.CurrentRow is DataGridViewRow row)
        {
            _grid.Rows.Remove(row);
            ValidateRows();
        }
    }

    private void AssignNextFreeAddress()
    {
        if (_grid.CurrentRow is not DataGridViewRow row)
        {
            return;
        }
        var profile = ProfileOfRow(row);
        int address = PatchValidator.NextFreeAddress(ReadRows(), _profiles, profile?.ChannelCount ?? 1, (string)row.Tag!);
        if (address < 0)
        {
            MessageBox.Show(this, "Plus aucune plage libre dans l'univers DMX.", "Univers plein", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        row.Cells[ColAddress].Value = address.ToString();
    }

    private void MoveRow(int delta)
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
        _grid.Rows.RemoveAt(index);
        _grid.Rows.Insert(target, row);
        SelectRow(row, editName: false);
    }

    private void Accept()
    {
        var rows = ReadRows();
        var problems = ValidateRows();
        if (problems.Count > 0 &&
            !Ui.Confirm(this, "Il reste des problèmes d'adressage :\n\n" + string.Join("\n", problems.Select(p => "• " + p.Message)) +
                              "\n\nValider quand même ?", "Conflits d'adressage"))
        {
            return;
        }

        ResultFixtures = rows;
        DialogResult = DialogResult.OK;
        Close();
    }
}
