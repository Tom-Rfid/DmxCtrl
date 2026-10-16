// =====================================================================================
//  MainForm.cs
// -------------------------------------------------------------------------------------
//  Main window:
//    - File menu: new configuration, open/import, save, save as/export, recent
//      configurations -> several configurations are possible (one per venue,
//      per kind of party...);
//    - Fixtures menu: add a fixture, patch/addressing, profile editor;
//    - Settings menu: interface language;
//    - connection bar: COM port, connect/disconnect, auto-connect at start-up.
//      If Bluetooth drops during the show, the program tries to reconnect by
//      itself every few seconds;
//    - tabs: "Show" (console), one tab per fixture of the patch, "Debug";
//    - status bar: open configuration, patch state, DEBUG MODE in red, messages.
// =====================================================================================

using System.ComponentModel;
using System.Diagnostics;
using DmxController.Core.Engine;
using DmxController.Core.Model;
using DmxController.Core.Protocol;
using DmxController.Core.Storage;

namespace DmxController.UI;

/// <summary>Main window: menus, connection bar, fog banner, tabs and status bar.</summary>
[DesignerCategory("Code")]
internal sealed class MainForm : Form
{
    private const string ShowFileFilter = "Configuration DMX (*.dmxshow.json)|*.dmxshow.json|Fichiers JSON (*.json)|*.json";

    private readonly ShowEngine _engine = new();
    private readonly DmxLink _link = new();
    private readonly ShowContext _ctx;
    private readonly AppSettings _settings;
    private string? _showPath;

    // Tabs
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    private readonly TabPage _liveTab = new(Tr.T("Soirée")) { Tag = "live" };
    private readonly TabPage _debugTab = new(Tr.T("Debug")) { Tag = "debug" };
    private readonly LivePanel _live;
    private readonly DebugPanel _debug;
    private readonly FogBanner _fogBanner;

    // Profile update messages (library) to show when opening.
    private List<string> _pendingUpgradeNotes = new();

    // Connection bar
    private readonly ComboBox _portCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Margin = new Padding(3, 7, 3, 3) };
    private readonly Button _btnConnect = new() { Text = Tr.T("Connecter"), AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
    private readonly Button _btnDisconnect = new() { Text = Tr.T("Déconnecter"), AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
    private readonly Label _lblLink = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(6, 10, 3, 3) };

    // Highly visible connection light: green square (connected), blinking orange (connecting), red (off).
    private readonly Label _linkLed = new()
    {
        AutoSize = false,
        Size = new Size(118, 28),
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(12, 5, 3, 3),
    };
    private readonly CheckBox _autoConnect = new() { Text = Tr.T("Connexion auto au démarrage"), AutoSize = true, Margin = new Padding(16, 9, 3, 3) };

    // Menu and status bar
    private readonly ToolStripMenuItem _recentMenu = new(Tr.T("Configurations récentes"));
    private readonly ToolStripStatusLabel _stShow = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _stPatch = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _stDebug = new() { Font = Ui.BoldFont };
    private readonly ToolStripStatusLabel _stMessage = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };
    private readonly byte[] _preview = DmxFrame.NewUniverse();

    // Automatic connection / reconnection
    private bool _connecting;
    private bool _wantConnected;     // the user wants to be connected (reconnect if the link drops)
    private string? _wantedPort;
    private DateTime _nextReconnect;
    private int _reconnectAttempts;
    private bool _closing;

    /// <summary>Builds the window, loads the last configuration and prepares the link and the timers.</summary>
    public MainForm()
    {
        Text = Tr.T("Contrôleur DMX");
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1540, 945);
        MinimumSize = new Size(1150, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true; // the window sees the keys before the controls (shortcuts of the Show tab)

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
            // Called from the send thread: switch back to the UI thread.
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

        // Hand-drawn tab titles: those of disabled fixtures are greyed out.
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.DrawItem += DrawTabTitle;

        var menu = BuildMenu();
        MainMenuStrip = menu;
        Controls.Add(_tabs);
        Controls.Add(_fogBanner); // below the connection bar, visible on every tab
        Controls.Add(BuildConnectionBar());
        Controls.Add(BuildStatusStrip());
        Controls.Add(menu);

        RebuildFixtureTabs();
        RefreshPorts();
        UpdateConnectionUi();
        UpdateTitle();
        UpdateStatusBar();
        Log(_showPath is null ? Tr.T("Configuration par défaut chargée.") : Tr.F("Configuration chargée : {0}", _showPath));

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
    //  Building
    // =================================================================================

    /// <summary>File, Fixtures, Settings and Help menus.</summary>
    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem(Tr.T("&Fichier"));
        file.DropDownItems.Add(Item(Tr.T("Nouvelle configuration (appareils par défaut)"), null, () => NewShow(empty: false)));
        file.DropDownItems.Add(Item(Tr.T("Nouvelle configuration vide (garde les profils)"), null, () => NewShow(empty: true)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item(Tr.T("&Ouvrir / importer…"), Keys.Control | Keys.O, () => OpenShow(null)));
        file.DropDownItems.Add(Item(Tr.T("&Enregistrer"), Keys.Control | Keys.S, () => SaveShow()));
        file.DropDownItems.Add(Item(Tr.T("Enregistrer &sous / exporter…"), Keys.Control | Keys.Shift | Keys.S, () => SaveShowAs()));
        file.DropDownItems.Add(_recentMenu);
        _recentMenu.DropDownOpening += (_, _) => BuildRecentMenu();
        BuildRecentMenu();
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item(Tr.T("Renommer la configuration…"), null, RenameShow));
        file.DropDownItems.Add(Item(Tr.T("Ouvrir le dossier des configurations"), null, OpenShowsFolder));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item(Tr.T("&Quitter"), null, Close));

        var fixtures = new ToolStripMenuItem(Tr.T("&Appareils"));
        fixtures.DropDownItems.Add(Item(Tr.T("&Ajouter un appareil (nouvel onglet)…"), Keys.Control | Keys.Shift | Keys.A, () => OpenPatch(addNew: true)));
        fixtures.DropDownItems.Add(Item(Tr.T("&Patch / adressage…"), Keys.Control | Keys.P, () => OpenPatch(addNew: false)));
        fixtures.DropDownItems.Add(Item(Tr.T("Éditeur de &profils (modules)…"), Keys.Control | Keys.M, OpenProfileEditor));
        fixtures.DropDownItems.Add(new ToolStripSeparator());
        fixtures.DropDownItems.Add(Item(Tr.T("Vérifier l'adressage"), null, () => ReportPatchProblems(onlyIfAny: false)));

        var settings = new ToolStripMenuItem(Tr.T("⚙ &Paramètres"));
        var language = new ToolStripMenuItem(Tr.T("🌐 Langue / Language"));
        foreach (var (code, name) in Tr.Languages)
        {
            var choice = new ToolStripMenuItem(name) { Checked = string.Equals(code, Tr.Language, StringComparison.OrdinalIgnoreCase) };
            choice.Click += (_, _) => ChangeLanguage(code);
            language.DropDownItems.Add(choice);
        }
        settings.DropDownItems.Add(language);

        var help = new ToolStripMenuItem(Tr.T("A&ide"));
        help.DropDownItems.Add(Item(Tr.T("Raccourcis clavier"), Keys.F1, ShowShortcuts));
        help.DropDownItems.Add(Item(Tr.T("À propos"), null, ShowAbout));

        menu.Items.AddRange(new ToolStripItem[] { file, fixtures, settings, help });
        return menu;
    }

    /// <summary>
    /// Saves the chosen interface language and offers to restart: windows read their texts
    /// when they are built, so the new language applies from the next start.
    /// </summary>
    private void ChangeLanguage(string code)
    {
        if (string.Equals(code, Tr.Language, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _settings.Language = code;
        SaveAppSettings();
        string message = code == Tr.French
            ? "La langue passera en français au prochain démarrage.\n\nRedémarrer maintenant ?"
            : "The language will switch to English at the next start.\n\nRestart now?";
        if (MessageBox.Show(this, message, "Langue / Language", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            Application.Restart(); // closes this window first (asks to save the configuration if needed)
        }
    }

    /// <summary>Menu item running <paramref name="action"/>, with an optional keyboard shortcut.</summary>
    private static ToolStripMenuItem Item(string text, Keys? shortcut, Action action)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => action());
        if (shortcut is Keys keys)
        {
            item.ShortcutKeys = keys;
        }
        return item;
    }

    /// <summary>Connection bar: COM port, connect / disconnect, link light and auto-connect box.</summary>
    private Control BuildConnectionBar()
    {
        var refresh = new Button { Text = Tr.T("Actualiser"), AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
        refresh.Click += (_, _) => RefreshPorts();
        _btnConnect.Click += async (_, _) =>
        {
            if (_portCombo.SelectedItem is string port)
            {
                await ConnectAsync(port, quiet: false);
            }
            else
            {
                MessageBox.Show(this, Tr.T("Choisis d'abord un port COM (le port « sortant » du boîtier, ex. COM6)."),
                    Tr.T("Port manquant"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        _btnDisconnect.Click += (_, _) => Disconnect();
        _autoConnect.Checked = _settings.AutoConnect;
        _autoConnect.CheckedChanged += (_, _) => SaveAppSettings();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 2, 6, 2), BackColor = Color.FromArgb(240, 242, 246) };
        bar.Controls.AddRange(new Control[]
        {
            Ui.Text(Tr.T("Boîtier Bluetooth (port COM) :"), bold: true), _portCombo, refresh, _btnConnect, _btnDisconnect, _linkLed, _lblLink, _autoConnect,
        });
        return bar;
    }

    /// <summary>Status bar: configuration, patch state, debug mode and last message.</summary>
    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip();
        strip.Items.AddRange(new ToolStripItem[] { _stShow, _stPatch, _stDebug, _stMessage });
        return strip;
    }

    // =================================================================================
    //  Configurations: new / open / save / export
    // =================================================================================

    /// <summary>Configuration to open at start-up: the last one used, otherwise the default one (created on the very first start).</summary>
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
                MessageBox.Show(Tr.F("La dernière configuration n'a pas pu être ouverte :\n{0}\n\n{1}\n\nOuverture de la configuration par défaut.", last, ex.Message),
                    Tr.T("Contrôleur DMX"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                // damaged file: start from a fresh configuration (without overwriting it)
                return (DefaultShow.Create(), null);
            }
        }

        // Very first start: the default configuration is created and saved.
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

    /// <summary>Replaces the open configuration by another one and rebuilds the interface.</summary>
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

    /// <summary>When there are unsaved changes, asks what to do. false = cancel the action.</summary>
    private bool ConfirmDiscard()
    {
        if (!_ctx.IsDirty)
        {
            return true;
        }

        var answer = MessageBox.Show(this, Tr.F("Enregistrer les modifications de « {0} » ?", _ctx.Show.Name),
            Tr.T("Modifications non enregistrées"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => SaveShow(),
            DialogResult.No => true,
            _ => false,
        };
    }

    /// <summary>New configuration: default fixtures, or empty while keeping the profiles.</summary>
    private void NewShow(bool empty)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        ShowConfig show;
        if (empty)
        {
            // New venue with the same kinds of fixtures: the profile library is kept.
            show = new ShowConfig
            {
                Name = Tr.T("Nouvelle soirée"),
                Profiles = _ctx.Show.Profiles.Select(p => ShowJson.Clone(p)).ToList(),
            };
        }
        else
        {
            show = DefaultShow.Create();
            show.Name = Tr.T("Nouvelle soirée");
        }

        ApplyShow(show, null);
        _ctx.MarkDirty();
        Log(empty ? Tr.T("Nouvelle configuration vide (ajoute des appareils : Appareils > Ajouter un appareil).") : Tr.T("Nouvelle configuration par défaut."));
    }

    /// <summary>Opens (imports) a configuration file; <paramref name="path"/> null shows the file picker.</summary>
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
                Title = Tr.T("Ouvrir / importer une configuration"),
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
            Log(Tr.F("Configuration ouverte : {0}", path));
            ReportUpgradeNotes();
            ReportPatchProblems(onlyIfAny: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Tr.F("Impossible d'ouvrir ce fichier :\n{0}\n\n{1}", path, ex.Message), Tr.T("Ouverture"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Saves the configuration (asks for a file the first time). false when cancelled or failed.</summary>
    private bool SaveShow()
    {
        if (_showPath is null)
        {
            return SaveShowAs();
        }

        try
        {
            // The console state is remembered to get it back at the next opening.
            _ctx.Show.LiveState = _engine.CaptureScene(Tr.T("État courant"));
            _ctx.Show.Settings.Master = _engine.Master;
            _ctx.Show.Settings.Bpm = _engine.Bpm;

            ShowStore.SaveShow(_ctx.Show, _showPath);
            _ctx.ClearDirty();
            RememberShow(_showPath);
            Log(Tr.F("Configuration enregistrée : {0}", _showPath));
            UpdateStatusBar();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Tr.F("Impossible d'enregistrer :\n{0}\n\n{1}", _showPath, ex.Message), Tr.T("Enregistrement"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>Saves the configuration under a new file (export). false when cancelled or failed.</summary>
    private bool SaveShowAs()
    {
        Directory.CreateDirectory(ShowStore.DefaultShowsFolder);
        using var dialog = new SaveFileDialog
        {
            Title = Tr.T("Enregistrer sous / exporter la configuration"),
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

        // The configuration name follows the file name.
        string fileName = Path.GetFileName(path);
        _ctx.Show.Name = fileName.EndsWith(ShowStore.ShowExtension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^ShowStore.ShowExtension.Length]
            : Path.GetFileNameWithoutExtension(fileName);
        _showPath = path;
        return SaveShow();
    }

    /// <summary>Renames the configuration (name shown in the title bar).</summary>
    private void RenameShow()
    {
        string? name = InputDialog.Ask(this, Tr.T("Renommer la configuration"), Tr.T("Nom de la configuration :"), _ctx.Show.Name);
        if (name is not null)
        {
            _ctx.Show.Name = name;
            _ctx.MarkDirty();
            UpdateStatusBar();
        }
    }

    /// <summary>Opens the configurations folder in Windows Explorer.</summary>
    private void OpenShowsFolder()
    {
        string folder = _showPath is not null ? Path.GetDirectoryName(_showPath)! : ShowStore.DefaultShowsFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    /// <summary>Remembers the file as the last one used and at the top of the recent list.</summary>
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

    /// <summary>Fills the "Recent configurations" menu.</summary>
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
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem(Tr.T("(aucune)")) { Enabled = false });
        }
    }

    /// <summary>Saves the settings of this PC (port, auto-connect, recent files, language).</summary>
    private void SaveAppSettings()
    {
        _settings.AutoConnect = _autoConnect.Checked;
        try
        {
            ShowStore.SaveAppSettings(_settings);
        }
        catch (Exception ex)
        {
            Log(Tr.F("Réglages non enregistrés : {0}", ex.Message));
        }
    }

    // =================================================================================
    //  Patch, profiles and fixture tabs
    // =================================================================================

    /// <summary>Opens the patch window; when it is accepted, applies the new patch and rebuilds the tabs.</summary>
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
            show.Profiles = dialog.ResultProfiles; // + models added from the library
            show.Fixtures = dialog.ResultFixtures;
        });

        // New fixtures are ticked straight away in the Show tab.
        var added = dialog.ResultFixtures.Where(f => !before.Contains(f.Id)).Select(f => f.Id).ToList();
        if (added.Count > 0)
        {
            _engine.SetSelection(_engine.GetSelection().Concat(added));
        }

        _ctx.MarkDirty();
        RebuildFixtureTabs();
        _live.ReloadAll();
        Log(Tr.F("Patch mis à jour : {0} appareil(s).", _ctx.Show.Fixtures.Count));
        ReportPatchProblems(onlyIfAny: true);
    }

    /// <summary>Opens the profile editor; when it is accepted, applies the edited profiles.</summary>
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
        Log(Tr.T("Profils mis à jour."));
        ReportPatchProblems(onlyIfAny: true);
    }

    /// <summary>Rebuilds one tab per fixture of the patch, between "Show" and "Debug".</summary>
    private void RebuildFixtureTabs()
    {
        string? selectedKey = _tabs.SelectedTab?.Tag as string;

        _tabs.SuspendLayout();
        foreach (var page in _tabs.TabPages.Cast<TabPage>().Where(p => p != _liveTab && p != _debugTab).ToList())
        {
            _tabs.TabPages.Remove(page);
            page.Dispose(); // also destroys the FixturePanel (which unsubscribes from the events)
        }

        int insertAt = 1;
        foreach (var fixture in _ctx.Show.Fixtures)
        {
            var profile = _ctx.ProfileOf(fixture);
            var page = new TabPage(TabTitle(fixture, profile)) { Tag = fixture.Id };
            if (profile is null)
            {
                page.Controls.Add(Ui.Text(Tr.F("Profil introuvable pour « {0} » : corrige-le dans Appareils > Patch.", fixture.Name), bold: true, color: Color.DarkRed));
            }
            else
            {
                // A tab suited to the model: fog, laser, or the generic tab (moving head, Spider, PAR...).
                Control panel = profile.IsFogMachine ? new FogPanel(_ctx, fixture, profile)
                    : profile.IsLaser ? new LaserPanel(_ctx, fixture, profile)
                    : new FixturePanel(_ctx, fixture, profile);
                page.Controls.Add(panel);
                if (!fixture.Enabled)
                {
                    // Disabled module: greyed-out tab, inactive controls, button to enable it again.
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

    /// <summary>Tab title: fixture name, "(disabled)" and "(?)" for an unchecked profile.</summary>
    private static string TabTitle(FixtureInstance fixture, FixtureProfile? profile) =>
        fixture.Name + (fixture.Enabled ? "" : Tr.T(" (désactivé)")) + (profile is not null && !profile.IsFullyVerified ? " (?)" : "");

    /// <summary>Banner shown on the tab of a disabled fixture, with a button to enable it.</summary>
    private Control BuildDisabledBar(FixtureInstance fixture)
    {
        var enable = new Button { Text = Tr.T("Activer ce module"), AutoSize = true, Margin = new Padding(12, 4, 3, 4) };
        enable.Click += (_, _) => SetFixtureEnabled(fixture.Id, true);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.FromArgb(235, 235, 235), Padding = new Padding(8, 2, 8, 2) };
        bar.Controls.Add(Ui.Text(Tr.T("Module DÉSACTIVÉ dans le patch : il n'envoie rien et n'est pas compté dans l'adressage."), bold: true, color: Color.DimGray));
        bar.Controls.Add(enable);
        return bar;
    }

    /// <summary>Enables or disables a fixture of the patch and rebuilds its tab.</summary>
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

    /// <summary>Tab titles: grey for a disabled fixture, bold for the active tab.</summary>
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

    /// <summary>Shows the profile update messages collected when the configuration was opened.</summary>
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
        MessageBox.Show(this, string.Join("\n\n", notes) + Tr.T("\n\nEnregistre la configuration (Ctrl+S) pour garder ces corrections."),
            Tr.T("Profils mis à jour"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Refreshes the tab titles (enabled state, checked profiles).</summary>
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

    /// <summary>Shows the addressing problems; with <paramref name="onlyIfAny"/>, says nothing when there is none.</summary>
    private void ReportPatchProblems(bool onlyIfAny)
    {
        var problems = PatchValidator.Validate(_ctx.Show);
        UpdateStatusBar();
        if (problems.Count == 0)
        {
            if (!onlyIfAny)
            {
                MessageBox.Show(this, Tr.T("Aucun conflit d'adressage."), Tr.T("Adressage"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        MessageBox.Show(this, Tr.T("Problèmes d'adressage :\n\n") + string.Join("\n", problems.Select(p => "• " + p.Message)) +
                              Tr.T("\n\nCorrige-les dans Appareils > Patch / adressage (Ctrl+P)."),
            Tr.T("Adressage"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // =================================================================================
    //  Connection to the box
    // =================================================================================

    /// <summary>Refreshes the COM port list and keeps the last port used selected.</summary>
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
    /// Opens the port in a background task: setting up a Bluetooth link can take several
    /// seconds, and the UI must not freeze meanwhile.
    /// </summary>
    private async Task<bool> ConnectAsync(string port, bool quiet)
    {
        if (_connecting || _link.IsConnected)
        {
            return false;
        }

        _connecting = true;
        UpdateConnectionUi();

        // TryConnectAndStart never throws: a box that is off or out of range just gives a
        // message (no exception crosses the await, so no more "unhandled exception").
        string? error = null;
        bool ok = await Task.Run(() => _link.TryConnectAndStart(port, out error));

        _connecting = false;
        if (ok)
        {
            _wantConnected = true;
            _wantedPort = port;
            _reconnectAttempts = 0;
            _settings.LastPort = port;
            SaveAppSettings();
            Log(Tr.F("Connecté au boîtier sur {0} (une trame toutes les {1} ms).", port, _link.SendIntervalMs));
        }
        else
        {
            Log(Tr.F("Échec de connexion sur {0} : {1}", port, error));
            if (!quiet)
            {
                MessageBox.Show(this, Tr.F("Impossible de se connecter sur {0} :\n{1}\n\n", port, error) +
                                      Tr.T("Vérifie que le boîtier est allumé, appairé, et que c'est bien le port « sortant »."),
                    Tr.T("Connexion"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        UpdateConnectionUi();
        return ok;
    }

    /// <summary>Disconnects on purpose (no automatic reconnection).</summary>
    private void Disconnect()
    {
        _wantConnected = false;
        _link.Disconnect();
        Log(Tr.T("Déconnecté."));
        UpdateConnectionUi();
    }

    /// <summary>Link lost during sending: logs it and lets the timer try to reconnect.</summary>
    private void OnConnectionLost(string message)
    {
        Log(Tr.F("Liaison perdue : {0}", message));
        _link.Disconnect(); // never throws (dead link included)
        _nextReconnect = DateTime.Now.AddSeconds(2);
        UpdateConnectionUi();
    }

    /// <summary>Enables the connection buttons according to the link state.</summary>
    private void UpdateConnectionUi()
    {
        bool connected = _link.IsConnected;
        _btnConnect.Enabled = !connected && !_connecting;
        _btnDisconnect.Enabled = connected || _wantConnected;
        _portCombo.Enabled = !connected && !_connecting;
        UpdateLinkLabel();
    }

    /// <summary>Text next to the link light (frames sent, reconnecting, not connected...).</summary>
    private void UpdateLinkLabel()
    {
        if (_link.IsSending)
        {
            _lblLink.Text = Tr.F("Connecté ({0}) — {1:N0} trames envoyées", _link.PortName, _link.FramesSent);
            _lblLink.ForeColor = Ui.OkColor;
        }
        else if (_connecting)
        {
            _lblLink.Text = _reconnectAttempts > 0 ? Tr.F("Reconnexion… (tentative {0})", _reconnectAttempts) : Tr.T("Connexion en cours…");
            _lblLink.ForeColor = Ui.WarningColor;
        }
        else if (_wantConnected)
        {
            _lblLink.Text = Tr.T("Liaison perdue — nouvelle tentative dans quelques secondes");
            _lblLink.ForeColor = Ui.WarningColor;
        }
        else
        {
            _lblLink.Text = Tr.T("Non connecté (aperçu seulement)");
            _lblLink.ForeColor = Color.DarkRed;
        }
        UpdateLinkLed();
    }

    /// <summary>Square light: steady green = connected; blinking orange = connecting / link lost; red = off.</summary>
    private void UpdateLinkLed()
    {
        bool blinkOn = DateTime.Now.Millisecond < 500; // 1 blink per second (100 ms timer)
        (string text, Color color) = _link.IsSending ? (Tr.T("● CONNECTÉ"), Color.FromArgb(0, 170, 70))
            : _connecting ? (Tr.T("● CONNEXION"), blinkOn ? Color.FromArgb(240, 150, 0) : Color.FromArgb(120, 80, 20))
            : _wantConnected ? (Tr.T("● PERDUE"), blinkOn ? Color.FromArgb(240, 150, 0) : Color.FromArgb(200, 40, 40))
            : (Tr.T("● OFF"), Color.FromArgb(200, 40, 40));
        if (_linkLed.Text != text)
        {
            _linkLed.Text = text;
        }
        if (_linkLed.BackColor != color)
        {
            _linkLed.BackColor = color;
        }
    }

    // =================================================================================
    //  UI timer (10 times per second)
    // =================================================================================

    /// <summary>UI timer (10 times/s): monitor, preview without a link, link light and automatic reconnection.</summary>
    private void OnUiTick()
    {
        // Without a link, the output is still computed so that the Debug monitor stays alive.
        if (!_link.IsSending)
        {
            Array.Clear(_preview);
            _engine.Render(_preview);
        }

        if (_tabs.SelectedTab == _debugTab)
        {
            _debug.UpdateMonitor(_engine.GetLastOutput());
        }

        // Automatic reconnection when Bluetooth dropped.
        if (_wantConnected && !_link.IsConnected && !_connecting && _wantedPort is not null && DateTime.Now >= _nextReconnect)
        {
            _reconnectAttempts++;
            _nextReconnect = DateTime.Now.AddSeconds(4);
            _ = ConnectAsync(_wantedPort, quiet: true);
        }

        UpdateLinkLabel();
    }

    // =================================================================================
    //  Title bar / status bar / log
    // =================================================================================

    /// <summary>Window title: configuration name, "*" when modified.</summary>
    private void UpdateTitle()
    {
        Text = Tr.F("Contrôleur DMX — {0}{1}", _ctx.Show.Name, (_ctx.IsDirty ? " *" : ""));
        UpdateStatusBar();
    }

    /// <summary>Status bar texts: configuration and file, patch problems, debug mode.</summary>
    private void UpdateStatusBar()
    {
        _stShow.Text = Tr.F("Configuration : {0}{1}  —  {2}", _ctx.Show.Name, (_ctx.IsDirty ? Tr.T(" (modifiée)") : ""), (_showPath ?? Tr.T("pas encore enregistrée")));

        int problems = PatchValidator.Validate(_ctx.Show).Count;
        _stPatch.Text = problems == 0 ? Tr.T("Patch : OK") : Tr.F("Patch : {0} problème(s)", problems);
        _stPatch.ForeColor = problems == 0 ? Ui.OkColor : Color.DarkRed;

        bool debug = _engine.DebugActive;
        _stDebug.Text = debug ? Tr.T("  MODE DEBUG ACTIF : la console Soirée est ignorée  ") : "";
        _stDebug.BackColor = debug ? Color.DarkRed : SystemColors.Control;
        _stDebug.ForeColor = Color.White;
    }

    /// <summary>Message in the status bar and in the Debug tab log.</summary>
    private void Log(string message)
    {
        _stMessage.Text = message.Length > 140 ? message[..140] + "…" : message;
        _debug?.AppendLog(message);
    }

    /// <summary>"Keyboard shortcuts" box.</summary>
    private void ShowShortcuts() => MessageBox.Show(this,
        Tr.T("Onglet Soirée :\n") +
        Tr.T("   Espace : blackout (bascule)\n") +
        Tr.T("   F (maintenu) : flash plein feu\n") +
        Tr.T("   T : tap tempo\n") +
        Tr.T("   1 à 9 : rappeler la scène n°\n") +
        Tr.T("   (si un champ de saisie a le focus, clique d'abord dans une zone vide)\n\n") +
        Tr.T("Partout :\n") +
        Tr.T("   Ctrl+S : enregistrer        Ctrl+Maj+S : enregistrer sous / exporter\n") +
        Tr.T("   Ctrl+O : ouvrir / importer\n") +
        Tr.T("   Ctrl+P : patch / adressage  Ctrl+Maj+A : ajouter un appareil\n") +
        Tr.T("   Ctrl+M : éditeur de profils"),
        Tr.T("Raccourcis clavier"), MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>"About" box.</summary>
    private void ShowAbout() => MessageBox.Show(this,
        Tr.T("Contrôleur DMX pour le boîtier Bluetooth « USB-RDM-DMX » (appli Android RY-AI).\n\n") +
        Tr.T("Protocole reconstitué par rétro-ingénierie de l'appli et confirmé sur le matériel :\n") +
        Tr.T("Bluetooth SPP, trames de 521 octets envoyées toutes les 100 ms, brouillage par tables.\n") +
        Tr.T("Détails : PROTOCOLE.md ; organisation du code : CLAUDE.md (dossier du projet).\n\n") +
        Tr.F("Configurations : {0}", ShowStore.DefaultShowsFolder),
        Tr.T("À propos"), MessageBoxButtons.OK, MessageBoxIcon.Information);

    // =================================================================================
    //  Keyboard and closing
    // =================================================================================

    /// <summary>Keys of the Show tab (blackout, flash, tap, scenes) when no text field has the focus.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_tabs.SelectedTab == _liveTab && e.Modifiers == Keys.None && !IsTypingInText() && _live.HandleKey(e.KeyCode, down: true))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Releasing F ends the flash.</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_tabs.SelectedTab == _liveTab && e.Modifiers == Keys.None && !IsTypingInText() && _live.HandleKey(e.KeyCode, down: false))
        {
            e.Handled = true;
        }
        base.OnKeyUp(e);
    }

    /// <summary>true when the focus is in a text field (keys are not stolen).</summary>
    private bool IsTypingInText()
    {
        Control? control = ActiveControl;
        while (control is ContainerControl container && container.ActiveControl is not null && control is not UpDownBase)
        {
            control = container.ActiveControl;
        }
        return control is TextBoxBase or UpDownBase || (control is ComboBox combo && combo.DropDownStyle != ComboBoxStyle.DropDownList);
    }

    /// <summary>Closing: offers to save, remembers the console state, disconnects the box.</summary>
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
