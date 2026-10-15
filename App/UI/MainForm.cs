// =====================================================================================
//  MainForm.cs
// -------------------------------------------------------------------------------------
//  Fenetre principale :
//    - menu Fichier : nouvelle config, ouvrir/importer, enregistrer, enregistrer
//      sous/exporter, configurations recentes -> plusieurs configs possibles
//      (une par salle, par type de soiree...) ;
//    - menu Appareils : ajouter un appareil, patch/adressage, editeur de profils ;
//    - barre de connexion : port COM, connecter/deconnecter, connexion auto au
//      demarrage. Si le Bluetooth decroche pendant la soiree, le programme tente
//      de se reconnecter tout seul toutes les quelques secondes ;
//    - onglets : "Soiree" (console), un onglet par appareil du patch, "Debug" ;
//    - barre d'etat : config ouverte, etat du patch, MODE DEBUG en rouge, messages.
// =====================================================================================

using System.ComponentModel;
using System.Diagnostics;
using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;
using DmxController.Core.Storage;

namespace DmxController.UI;

[DesignerCategory("Code")]
internal sealed class MainForm : Form
{
    private const string ShowFileFilter = "Configuration DMX (*.dmxshow.json)|*.dmxshow.json|Fichiers JSON (*.json)|*.json";

    private readonly ShowEngine _engine = new();
    private readonly DmxLink _link = new();
    private readonly ShowContext _ctx;
    private readonly AppSettings _settings;
    private string? _showPath;

    // Onglets
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    private readonly TabPage _liveTab = new("Soirée") { Tag = "live" };
    private readonly TabPage _debugTab = new("Debug") { Tag = "debug" };
    private readonly LivePanel _live;
    private readonly DebugPanel _debug;
    private readonly FogBanner _fogBanner;

    // Messages de mise a jour des profils (bibliotheque) a afficher a l'ouverture.
    private List<string> _pendingUpgradeNotes = new();

    // Barre de connexion
    private readonly ComboBox _portCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Margin = new Padding(3, 7, 3, 3) };
    private readonly Button _btnConnect = new() { Text = "Connecter", AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
    private readonly Button _btnDisconnect = new() { Text = "Déconnecter", AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
    private readonly Label _lblLink = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(12, 10, 3, 3) };
    private readonly CheckBox _autoConnect = new() { Text = "Connexion auto au démarrage", AutoSize = true, Margin = new Padding(16, 9, 3, 3) };

    // Menu et barre d'etat
    private readonly ToolStripMenuItem _recentMenu = new("Configurations récentes");
    private readonly ToolStripStatusLabel _stShow = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _stPatch = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _stDebug = new() { Font = Ui.BoldFont };
    private readonly ToolStripStatusLabel _stMessage = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };
    private readonly byte[] _preview = DmxFrame.NewUniverse();

    // Connexion / reconnexion automatique
    private bool _connecting;
    private bool _wantConnected;     // l'utilisateur veut etre connecte (reconnexion si coupure)
    private string? _wantedPort;
    private DateTime _nextReconnect;
    private int _reconnectAttempts;
    private bool _closing;

    public MainForm()
    {
        Text = "Contrôleur DMX";
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1400, 900);
        MinimumSize = new Size(1150, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true; // la fenetre voit les touches avant les controles (raccourcis de l'onglet Soiree)

        _settings = ShowStore.LoadAppSettings();
        var (show, path) = LoadInitialShow();
        _showPath = path;
        _pendingUpgradeNotes = FixtureLibrary.UpgradeProfiles(show);

        _ctx = new ShowContext(_engine, _link, show);
        _engine.LoadShow(show);
        _link.Source = _engine;
        _link.SendIntervalMs = show.Settings.SendIntervalMs;
        _link.ConnectionLost += (_, message) =>
        {
            // Appele depuis le thread d'envoi : on repasse sur le thread de l'IHM.
            if (!_closing && IsHandleCreated)
            {
                BeginInvoke(new Action(() => OnConnectionLost(message)));
            }
        };

        _ctx.DirtyChanged += UpdateTitle;
        _ctx.StatusMessage += Log;
        _ctx.DebugModeChanged += UpdateStatusBar;
        _ctx.ProfileDetailsChanged += () =>
        {
            UpdateTabTitles();
            _debug?.RebuildLabels();
        };

        _live = new LivePanel(_ctx);
        _debug = new DebugPanel(_ctx);
        _fogBanner = new FogBanner(_ctx);
        _liveTab.Controls.Add(_live);
        _debugTab.Controls.Add(_debug);
        _tabs.TabPages.Add(_liveTab);
        _tabs.TabPages.Add(_debugTab);

        // Titres d'onglets dessines a la main : ceux des appareils desactives sont grises.
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.DrawItem += DrawTabTitle;

        var menu = BuildMenu();
        MainMenuStrip = menu;
        Controls.Add(_tabs);
        Controls.Add(_fogBanner); // sous la barre de connexion, visible sur tous les onglets
        Controls.Add(BuildConnectionBar());
        Controls.Add(BuildStatusStrip());
        Controls.Add(menu);

        RebuildFixtureTabs();
        RefreshPorts();
        UpdateConnectionUi();
        UpdateTitle();
        UpdateStatusBar();
        Log(_showPath is null ? "Configuration par défaut chargée." : $"Configuration chargée : {_showPath}");

        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        Shown += async (_, _) =>
        {
            ReportUpgradeNotes();
            ReportPatchProblems(onlyIfAny: true);
            if (_settings.AutoConnect && _settings.LastPort is string port && _portCombo.Items.Contains(port))
            {
                await ConnectAsync(port, quiet: true);
            }
        };
    }

    // =================================================================================
    //  Construction
    // =================================================================================

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&Fichier");
        file.DropDownItems.Add(Item("Nouvelle configuration (appareils par défaut)", null, () => NewShow(empty: false)));
        file.DropDownItems.Add(Item("Nouvelle configuration vide (garde les profils)", null, () => NewShow(empty: true)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("&Ouvrir / importer…", Keys.Control | Keys.O, () => OpenShow(null)));
        file.DropDownItems.Add(Item("&Enregistrer", Keys.Control | Keys.S, () => SaveShow()));
        file.DropDownItems.Add(Item("Enregistrer &sous / exporter…", Keys.Control | Keys.Shift | Keys.S, () => SaveShowAs()));
        file.DropDownItems.Add(_recentMenu);
        _recentMenu.DropDownOpening += (_, _) => BuildRecentMenu();
        BuildRecentMenu();
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Renommer la configuration…", null, RenameShow));
        file.DropDownItems.Add(Item("Ouvrir le dossier des configurations", null, OpenShowsFolder));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("&Quitter", null, Close));

        var fixtures = new ToolStripMenuItem("&Appareils");
        fixtures.DropDownItems.Add(Item("&Ajouter un appareil (nouvel onglet)…", Keys.Control | Keys.Shift | Keys.A, () => OpenPatch(addNew: true)));
        fixtures.DropDownItems.Add(Item("&Patch / adressage…", Keys.Control | Keys.P, () => OpenPatch(addNew: false)));
        fixtures.DropDownItems.Add(Item("Éditeur de &profils (modules)…", Keys.Control | Keys.M, OpenProfileEditor));
        fixtures.DropDownItems.Add(new ToolStripSeparator());
        fixtures.DropDownItems.Add(Item("Vérifier l'adressage", null, () => ReportPatchProblems(onlyIfAny: false)));

        var help = new ToolStripMenuItem("A&ide");
        help.DropDownItems.Add(Item("Raccourcis clavier", Keys.F1, ShowShortcuts));
        help.DropDownItems.Add(Item("À propos", null, ShowAbout));

        menu.Items.AddRange(new ToolStripItem[] { file, fixtures, help });
        return menu;
    }

    private static ToolStripMenuItem Item(string text, Keys? shortcut, Action action)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => action());
        if (shortcut is Keys keys)
        {
            item.ShortcutKeys = keys;
        }
        return item;
    }

    private Control BuildConnectionBar()
    {
        var refresh = new Button { Text = "Actualiser", AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
        refresh.Click += (_, _) => RefreshPorts();
        _btnConnect.Click += async (_, _) =>
        {
            if (_portCombo.SelectedItem is string port)
            {
                await ConnectAsync(port, quiet: false);
            }
            else
            {
                MessageBox.Show(this, "Choisis d'abord un port COM (le port « sortant » du boîtier, ex. COM6).",
                    "Port manquant", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        _btnDisconnect.Click += (_, _) => Disconnect();
        _autoConnect.Checked = _settings.AutoConnect;
        _autoConnect.CheckedChanged += (_, _) => SaveAppSettings();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 2, 6, 2), BackColor = Color.FromArgb(240, 242, 246) };
        bar.Controls.AddRange(new Control[]
        {
            Ui.Text("Boîtier Bluetooth (port COM) :", bold: true), _portCombo, refresh, _btnConnect, _btnDisconnect, _lblLink, _autoConnect,
        });
        return bar;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip();
        strip.Items.AddRange(new ToolStripItem[] { _stShow, _stPatch, _stDebug, _stMessage });
        return strip;
    }

    // =================================================================================
    //  Configurations : nouvelle / ouvrir / enregistrer / exporter
    // =================================================================================

    private (ShowConfig Show, string? Path) LoadInitialShow()
    {
        if (_settings.LastShowPath is string last && File.Exists(last))
        {
            try
            {
                return (ShowStore.LoadShow(last), last);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"La dernière configuration n'a pas pu être ouverte :\n{last}\n\n{ex.Message}\n\nOuverture de la configuration par défaut.",
                    "Contrôleur DMX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        string defaultPath = Path.Combine(ShowStore.DefaultShowsFolder, "Soirée par défaut" + ShowStore.ShowExtension);
        if (File.Exists(defaultPath))
        {
            try
            {
                return (ShowStore.LoadShow(defaultPath), defaultPath);
            }
            catch (Exception)
            {
                // fichier abime : on repart d'une config neuve (sans l'ecraser)
                return (DefaultShow.Create(), null);
            }
        }

        // Tout premier lancement : on cree et on enregistre la configuration par defaut.
        var show = DefaultShow.Create();
        try
        {
            ShowStore.SaveShow(show, defaultPath);
            return (show, defaultPath);
        }
        catch (Exception)
        {
            return (show, null);
        }
    }

    /// <summary>Remplace la configuration ouverte par une autre et reconstruit l'interface.</summary>
    private void ApplyShow(ShowConfig show, string? path)
    {
        _engine.LoadShow(show);
        _ctx.Show = show;
        _showPath = path;
        _link.SendIntervalMs = show.Settings.SendIntervalMs;

        RebuildFixtureTabs();
        _live.ReloadAll();
        _debug.ReloadSettings();
        _debug.RebuildLabels();
        _ctx.ClearDirty();

        if (path is not null)
        {
            RememberShow(path);
        }
        UpdateTitle();
        UpdateStatusBar();
    }

    /// <summary>Si des modifications ne sont pas enregistrees, demande quoi faire. false = annuler l'action.</summary>
    private bool ConfirmDiscard()
    {
        if (!_ctx.IsDirty)
        {
            return true;
        }

        var answer = MessageBox.Show(this, $"Enregistrer les modifications de « {_ctx.Show.Name} » ?",
            "Modifications non enregistrées", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => SaveShow(),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void NewShow(bool empty)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        ShowConfig show;
        if (empty)
        {
            // Nouvelle salle avec les memes types d'appareils : on garde la bibliotheque de profils.
            show = new ShowConfig
            {
                Name = "Nouvelle soirée",
                Profiles = _ctx.Show.Profiles.Select(p => ShowJson.Clone(p)).ToList(),
            };
        }
        else
        {
            show = DefaultShow.Create();
            show.Name = "Nouvelle soirée";
        }

        ApplyShow(show, null);
        _ctx.MarkDirty();
        Log(empty ? "Nouvelle configuration vide (ajoute des appareils : Appareils > Ajouter un appareil)." : "Nouvelle configuration par défaut.");
    }

    private void OpenShow(string? path)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        if (path is null)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Ouvrir / importer une configuration",
                Filter = ShowFileFilter,
                InitialDirectory = Directory.Exists(ShowStore.DefaultShowsFolder) ? ShowStore.DefaultShowsFolder : "",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            path = dialog.FileName;
        }

        try
        {
            var show = ShowStore.LoadShow(path);
            _pendingUpgradeNotes = FixtureLibrary.UpgradeProfiles(show);
            ApplyShow(show, path);
            Log($"Configuration ouverte : {path}");
            ReportUpgradeNotes();
            ReportPatchProblems(onlyIfAny: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible d'ouvrir ce fichier :\n{path}\n\n{ex.Message}", "Ouverture",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool SaveShow()
    {
        if (_showPath is null)
        {
            return SaveShowAs();
        }

        try
        {
            // On memorise l'etat de la console pour le retrouver a la prochaine ouverture.
            _ctx.Show.LiveState = _engine.CaptureScene("État courant");
            _ctx.Show.Settings.Master = _engine.Master;
            _ctx.Show.Settings.Bpm = _engine.Bpm;

            ShowStore.SaveShow(_ctx.Show, _showPath);
            _ctx.ClearDirty();
            RememberShow(_showPath);
            Log($"Configuration enregistrée : {_showPath}");
            UpdateStatusBar();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible d'enregistrer :\n{_showPath}\n\n{ex.Message}", "Enregistrement",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool SaveShowAs()
    {
        Directory.CreateDirectory(ShowStore.DefaultShowsFolder);
        using var dialog = new SaveFileDialog
        {
            Title = "Enregistrer sous / exporter la configuration",
            Filter = ShowFileFilter,
            InitialDirectory = _showPath is null ? ShowStore.DefaultShowsFolder : Path.GetDirectoryName(_showPath),
            FileName = ProfileEditorDialog.SafeFileName(_ctx.Show.Name) + ShowStore.ShowExtension,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        string path = dialog.FileName;
        if (!path.EndsWith(ShowStore.ShowExtension, StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            path += ShowStore.ShowExtension;
        }

        // Le nom de la configuration suit le nom du fichier.
        string fileName = Path.GetFileName(path);
        _ctx.Show.Name = fileName.EndsWith(ShowStore.ShowExtension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^ShowStore.ShowExtension.Length]
            : Path.GetFileNameWithoutExtension(fileName);
        _showPath = path;
        return SaveShow();
    }

    private void RenameShow()
    {
        string? name = InputDialog.Ask(this, "Renommer la configuration", "Nom de la configuration :", _ctx.Show.Name);
        if (name is not null)
        {
            _ctx.Show.Name = name;
            _ctx.MarkDirty();
            UpdateStatusBar();
        }
    }

    private void OpenShowsFolder()
    {
        string folder = _showPath is not null ? Path.GetDirectoryName(_showPath)! : ShowStore.DefaultShowsFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    private void RememberShow(string path)
    {
        _settings.LastShowPath = path;
        _settings.RecentShows.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentShows.Insert(0, path);
        if (_settings.RecentShows.Count > 8)
        {
            _settings.RecentShows.RemoveRange(8, _settings.RecentShows.Count - 8);
        }
        SaveAppSettings();
    }

    private void BuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        foreach (string path in _settings.RecentShows.Where(File.Exists))
        {
            var item = new ToolStripMenuItem(Path.GetFileName(path)) { ToolTipText = path };
            string target = path;
            item.Click += (_, _) => OpenShow(target);
            _recentMenu.DropDownItems.Add(item);
        }
        if (_recentMenu.DropDownItems.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(aucune)") { Enabled = false });
        }
    }

    private void SaveAppSettings()
    {
        _settings.AutoConnect = _autoConnect.Checked;
        try
        {
            ShowStore.SaveAppSettings(_settings);
        }
        catch (Exception ex)
        {
            Log($"Réglages non enregistrés : {ex.Message}");
        }
    }

    // =================================================================================
    //  Patch, profils et onglets d'appareils
    // =================================================================================

    private void OpenPatch(bool addNew)
    {
        using var dialog = new PatchDialog(_ctx.Show, addNew);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var before = _ctx.Show.Fixtures.Select(f => f.Id).ToHashSet();
        _engine.EditShow(show =>
        {
            show.Profiles = dialog.ResultProfiles; // + modeles ajoutes depuis la bibliotheque
            show.Fixtures = dialog.ResultFixtures;
        });

        // Les nouveaux appareils sont coches d'office dans l'onglet Soiree.
        var added = dialog.ResultFixtures.Where(f => !before.Contains(f.Id)).Select(f => f.Id).ToList();
        if (added.Count > 0)
        {
            _engine.SetSelection(_engine.GetSelection().Concat(added));
        }

        _ctx.MarkDirty();
        RebuildFixtureTabs();
        _live.ReloadAll();
        Log($"Patch mis à jour : {_ctx.Show.Fixtures.Count} appareil(s).");
        ReportPatchProblems(onlyIfAny: true);
    }

    private void OpenProfileEditor()
    {
        using var dialog = new ProfileEditorDialog(_ctx.Show);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _engine.EditShow(show => show.Profiles = dialog.ResultProfiles);
        _ctx.MarkDirty();
        RebuildFixtureTabs();
        _live.ReloadAll();
        Log("Profils mis à jour.");
        ReportPatchProblems(onlyIfAny: true);
    }

    /// <summary>Recree un onglet par appareil du patch, entre "Soiree" et "Debug".</summary>
    private void RebuildFixtureTabs()
    {
        string? selectedKey = _tabs.SelectedTab?.Tag as string;

        _tabs.SuspendLayout();
        foreach (var page in _tabs.TabPages.Cast<TabPage>().Where(p => p != _liveTab && p != _debugTab).ToList())
        {
            _tabs.TabPages.Remove(page);
            page.Dispose(); // detruit aussi le FixturePanel (qui se desabonne des evenements)
        }

        int insertAt = 1;
        foreach (var fixture in _ctx.Show.Fixtures)
        {
            var profile = _ctx.ProfileOf(fixture);
            var page = new TabPage(TabTitle(fixture, profile)) { Tag = fixture.Id };
            if (profile is null)
            {
                page.Controls.Add(Ui.Text($"Profil introuvable pour « {fixture.Name} » : corrige-le dans Appareils > Patch.", bold: true, color: Color.DarkRed));
            }
            else
            {
                // Un onglet adapte au modele : fumee, laser, ou onglet generique (lyre, Spider, PAR...).
                Control panel = profile.IsFogMachine ? new FogPanel(_ctx, fixture, profile)
                    : profile.IsLaser ? new LaserPanel(_ctx, fixture, profile)
                    : new FixturePanel(_ctx, fixture, profile);
                page.Controls.Add(panel);
                if (!fixture.Enabled)
                {
                    // Module desactive : onglet grise, commandes inactives, bouton pour le reactiver.
                    panel.Enabled = false;
                    page.Controls.Add(BuildDisabledBar(fixture));
                }
            }
            _tabs.TabPages.Insert(insertAt++, page);
        }
        _tabs.ResumeLayout();
        _fogBanner.Rebuild();

        foreach (TabPage page in _tabs.TabPages)
        {
            if (Equals(page.Tag, selectedKey))
            {
                _tabs.SelectedTab = page;
            }
        }

        _debug.RebuildLabels();
        UpdateStatusBar();
    }

    private static string TabTitle(FixtureInstance fixture, FixtureProfile? profile) =>
        fixture.Name + (fixture.Enabled ? "" : " (désactivé)") + (profile is not null && !profile.IsFullyVerified ? " (?)" : "");

    private Control BuildDisabledBar(FixtureInstance fixture)
    {
        var enable = new Button { Text = "Activer ce module", AutoSize = true, Margin = new Padding(12, 4, 3, 4) };
        enable.Click += (_, _) => SetFixtureEnabled(fixture.Id, true);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.FromArgb(235, 235, 235), Padding = new Padding(8, 2, 8, 2) };
        bar.Controls.Add(Ui.Text("Module DÉSACTIVÉ dans le patch : il n'envoie rien et n'est pas compté dans l'adressage.", bold: true, color: Color.DimGray));
        bar.Controls.Add(enable);
        return bar;
    }

    private void SetFixtureEnabled(string fixtureId, bool enabled)
    {
        _engine.EditShow(show =>
        {
            var fixture = show.Fixtures.FirstOrDefault(f => f.Id == fixtureId);
            if (fixture is not null)
            {
                fixture.Enabled = enabled;
            }
        });
        _ctx.MarkDirty();
        RebuildFixtureTabs();
        _live.ReloadAll();
        ReportPatchProblems(onlyIfAny: true);
    }

    /// <summary>Titres d'onglets : gris pour un appareil desactive, gras pour l'onglet actif.</summary>
    private void DrawTabTitle(object? sender, DrawItemEventArgs e)
    {
        var page = _tabs.TabPages[e.Index];
        var fixture = _ctx.Show.Fixtures.FirstOrDefault(f => Equals(f.Id, page.Tag));
        bool selected = e.Index == _tabs.SelectedIndex;
        bool disabled = fixture is { Enabled: false };

        e.Graphics.FillRectangle(selected ? SystemBrushes.Window : SystemBrushes.Control, e.Bounds);
        var color = disabled ? Color.Gray : SystemColors.ControlText;
        TextRenderer.DrawText(e.Graphics, page.Text, selected ? Ui.BoldFont : Font, e.Bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private void ReportUpgradeNotes()
    {
        if (_pendingUpgradeNotes.Count == 0)
        {
            return;
        }
        var notes = _pendingUpgradeNotes;
        _pendingUpgradeNotes = new();
        foreach (string note in notes)
        {
            Log(note);
        }
        _ctx.MarkDirty();
        MessageBox.Show(this, string.Join("\n\n", notes) + "\n\nEnregistre la configuration (Ctrl+S) pour garder ces corrections.",
            "Profils mis à jour", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateTabTitles()
    {
        foreach (TabPage page in _tabs.TabPages)
        {
            var fixture = _ctx.Show.Fixtures.FirstOrDefault(f => Equals(f.Id, page.Tag));
            if (fixture is not null)
            {
                page.Text = TabTitle(fixture, _ctx.ProfileOf(fixture));
            }
        }
    }

    private void ReportPatchProblems(bool onlyIfAny)
    {
        var problems = PatchValidator.Validate(_ctx.Show);
        UpdateStatusBar();
        if (problems.Count == 0)
        {
            if (!onlyIfAny)
            {
                MessageBox.Show(this, "Aucun conflit d'adressage.", "Adressage", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        MessageBox.Show(this, "Problèmes d'adressage :\n\n" + string.Join("\n", problems.Select(p => "• " + p.Message)) +
                              "\n\nCorrige-les dans Appareils > Patch / adressage (Ctrl+P).",
            "Adressage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // =================================================================================
    //  Connexion au boitier
    // =================================================================================

    private void RefreshPorts()
    {
        string? previous = _portCombo.SelectedItem as string ?? _settings.LastPort;
        _portCombo.Items.Clear();
        _portCombo.Items.AddRange(DmxLink.GetPortNames());

        if (previous is not null && _portCombo.Items.Contains(previous))
        {
            _portCombo.SelectedItem = previous;
        }
        else if (_portCombo.Items.Count > 0)
        {
            _portCombo.SelectedIndex = _portCombo.Items.Count - 1;
        }
    }

    /// <summary>
    /// Ouverture du port dans une tache de fond : l'etablissement d'une liaison
    /// Bluetooth peut prendre plusieurs secondes, l'IHM ne doit pas geler pendant ce temps.
    /// </summary>
    private async Task<bool> ConnectAsync(string port, bool quiet)
    {
        if (_connecting || _link.IsConnected)
        {
            return false;
        }

        _connecting = true;
        UpdateConnectionUi();
        try
        {
            await Task.Run(() =>
            {
                _link.Connect(port);
                _link.StartSending();
            });

            _wantConnected = true;
            _wantedPort = port;
            _reconnectAttempts = 0;
            _settings.LastPort = port;
            SaveAppSettings();
            Log($"Connecté au boîtier sur {port} (une trame toutes les {_link.SendIntervalMs} ms).");
            return true;
        }
        catch (Exception ex)
        {
            try { _link.Disconnect(); } catch (Exception) { /* deja ferme */ }
            Log($"Échec de connexion sur {port} : {ex.Message}");
            if (!quiet)
            {
                MessageBox.Show(this, $"Impossible de se connecter sur {port} :\n{ex.Message}\n\n" +
                                      "Vérifie que le boîtier est allumé, appairé, et que c'est bien le port « sortant ».",
                    "Connexion", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }
        finally
        {
            _connecting = false;
            UpdateConnectionUi();
        }
    }

    private void Disconnect()
    {
        _wantConnected = false;
        _link.Disconnect();
        Log("Déconnecté.");
        UpdateConnectionUi();
    }

    private void OnConnectionLost(string message)
    {
        Log($"Liaison perdue : {message}");
        try { _link.Disconnect(); } catch (Exception) { /* deja coupe */ }
        _nextReconnect = DateTime.Now.AddSeconds(2);
        UpdateConnectionUi();
    }

    private void UpdateConnectionUi()
    {
        bool connected = _link.IsConnected;
        _btnConnect.Enabled = !connected && !_connecting;
        _btnDisconnect.Enabled = connected || _wantConnected;
        _portCombo.Enabled = !connected && !_connecting;
        UpdateLinkLabel();
    }

    private void UpdateLinkLabel()
    {
        if (_link.IsSending)
        {
            _lblLink.Text = $"Connecté ({_link.PortName}) — {_link.FramesSent:N0} trames envoyées";
            _lblLink.ForeColor = Ui.OkColor;
        }
        else if (_connecting)
        {
            _lblLink.Text = _reconnectAttempts > 0 ? $"Reconnexion… (tentative {_reconnectAttempts})" : "Connexion en cours…";
            _lblLink.ForeColor = Ui.WarningColor;
        }
        else if (_wantConnected)
        {
            _lblLink.Text = "Liaison perdue — nouvelle tentative dans quelques secondes";
            _lblLink.ForeColor = Ui.WarningColor;
        }
        else
        {
            _lblLink.Text = "Non connecté (aperçu seulement)";
            _lblLink.ForeColor = Color.DarkRed;
        }
    }

    // =================================================================================
    //  Minuteur de l'IHM (10 fois par seconde)
    // =================================================================================

    private void OnUiTick()
    {
        // Sans liaison, on calcule quand meme la sortie pour que le moniteur Debug reste vivant.
        if (!_link.IsSending)
        {
            Array.Clear(_preview);
            _engine.Render(_preview);
        }

        if (_tabs.SelectedTab == _debugTab)
        {
            _debug.UpdateMonitor(_engine.GetLastOutput());
        }

        // Reconnexion automatique si le Bluetooth a decroche.
        if (_wantConnected && !_link.IsConnected && !_connecting && _wantedPort is not null && DateTime.Now >= _nextReconnect)
        {
            _reconnectAttempts++;
            _nextReconnect = DateTime.Now.AddSeconds(4);
            _ = ConnectAsync(_wantedPort, quiet: true);
        }

        UpdateLinkLabel();
    }

    // =================================================================================
    //  Barre de titre / barre d'etat / journal
    // =================================================================================

    private void UpdateTitle()
    {
        Text = $"Contrôleur DMX — {_ctx.Show.Name}{(_ctx.IsDirty ? " *" : "")}";
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        _stShow.Text = $"Configuration : {_ctx.Show.Name}{(_ctx.IsDirty ? " (modifiée)" : "")}  —  {_showPath ?? "pas encore enregistrée"}";

        int problems = PatchValidator.Validate(_ctx.Show).Count;
        _stPatch.Text = problems == 0 ? "Patch : OK" : $"Patch : {problems} problème(s)";
        _stPatch.ForeColor = problems == 0 ? Ui.OkColor : Color.DarkRed;

        bool debug = _engine.DebugActive;
        _stDebug.Text = debug ? "  MODE DEBUG ACTIF : la console Soirée est ignorée  " : "";
        _stDebug.BackColor = debug ? Color.DarkRed : SystemColors.Control;
        _stDebug.ForeColor = Color.White;
    }

    private void Log(string message)
    {
        _stMessage.Text = message.Length > 140 ? message[..140] + "…" : message;
        _debug?.AppendLog(message);
    }

    private void ShowShortcuts() => MessageBox.Show(this,
        "Onglet Soirée :\n" +
        "   Espace : blackout (bascule)\n" +
        "   F (maintenu) : flash plein feu\n" +
        "   T : tap tempo\n" +
        "   1 à 9 : rappeler la scène n°\n" +
        "   (si un champ de saisie a le focus, clique d'abord dans une zone vide)\n\n" +
        "Partout :\n" +
        "   Ctrl+S : enregistrer        Ctrl+Maj+S : enregistrer sous / exporter\n" +
        "   Ctrl+O : ouvrir / importer\n" +
        "   Ctrl+P : patch / adressage  Ctrl+Maj+A : ajouter un appareil\n" +
        "   Ctrl+M : éditeur de profils",
        "Raccourcis clavier", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowAbout() => MessageBox.Show(this,
        "Contrôleur DMX pour le boîtier Bluetooth « USB-RDM-DMX » (appli Android RY-AI).\n\n" +
        "Protocole reconstitué par rétro-ingénierie de l'appli et confirmé sur le matériel :\n" +
        "Bluetooth SPP, trames de 521 octets envoyées toutes les 100 ms, brouillage par tables.\n" +
        "Détails : PROTOCOLE.md ; organisation du code : CLAUDE.md (dossier du projet).\n\n" +
        $"Configurations : {ShowStore.DefaultShowsFolder}",
        "À propos", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // =================================================================================
    //  Clavier et fermeture
    // =================================================================================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_tabs.SelectedTab == _liveTab && e.Modifiers == Keys.None && !IsTypingInText() && _live.HandleKey(e.KeyCode, down: true))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_tabs.SelectedTab == _liveTab && e.Modifiers == Keys.None && !IsTypingInText() && _live.HandleKey(e.KeyCode, down: false))
        {
            e.Handled = true;
        }
        base.OnKeyUp(e);
    }

    /// <summary>true si le focus est dans une zone de saisie (on ne vole pas les touches).</summary>
    private bool IsTypingInText()
    {
        Control? control = ActiveControl;
        while (control is ContainerControl container && container.ActiveControl is not null && control is not UpDownBase)
        {
            control = container.ActiveControl;
        }
        return control is TextBoxBase or UpDownBase || (control is ComboBox combo && combo.DropDownStyle != ComboBoxStyle.DropDownList);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        _closing = true;
        _uiTimer.Stop();
        _wantConnected = false;
        _link.Dispose();
        _ctx.Mic.Dispose();
        SaveAppSettings();
        base.OnFormClosing(e);
    }
}
