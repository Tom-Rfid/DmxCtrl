// =====================================================================================
//  SweepPanel.cs
// -------------------------------------------------------------------------------------
//  "Sweep" tab: moving heads (and Spiders) swinging left / right TOGETHER.
//
//   +-------------------------------------------------+-------------------------+
//   | [Moving heads] [Moving heads + Spider]  (pages) |  Automatic sweep        |
//   |                                                 |   start / stop          |
//   |   XY pad (left / right, up / down)   dimmers:   |   Delay | Tempo | Mic   |
//   |                                      All Heads  |   beats per swing       |
//   |                                      Spider     |   microphone, BPM       |
//   |   [x] crossed heads  [x] mirrored Spider        |   amplitude             |
//   |   fixtures: driven? + stage side (L / C / R)    |                         |
//   +-------------------------------------------------+-------------------------+
//
//  The page decides whether the Spider takes part (GroupSweepSettings.IncludeSpider).
//  "Crossed": the left fixtures move the opposite way to the right ones (they split
//  apart and come back to the middle); the stage side of each fixture is set here or
//  in the patch. Settings are saved with the configuration (ShowSettings.GroupSweep).
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Engine;
using DmxController.Core.Model;

namespace DmxController.UI;

/// <summary>Sweep tab: moving heads and Spiders swinging left / right together, by hand or automatically.</summary>
[DesignerCategory("Code")]
internal sealed class SweepPanel : UserControl
{
    private enum Mode { Delay, Bpm, Mic }

    private readonly ShowContext _ctx;
    private readonly ToolTip _tips = new();

    // Left: pages + shared content (moved into the selected page)
    private readonly TabControl _pages = new() { Dock = DockStyle.Fill };
    private readonly TabPage _headsPage = new(Tr.T("Lyres"));
    private readonly TabPage _allPage = new(Tr.T("Lyres + Spider"));
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly XYPad _pad = new() { Size = new Size(280, 280), XLabel = Tr.T("← gauche · droite →"), YLabel = Tr.T("Tilt ↓ (lyres)") };
    private readonly VerticalFader _dimAll = new() { Size = new Size(56, 220) };
    private readonly VerticalFader _dimHeads = new() { Size = new Size(56, 220) };
    private readonly VerticalFader _dimWings = new() { Size = new Size(56, 220), FillColor = Color.FromArgb(150, 200, 255) };
    private readonly Control _wingsColumn;
    private readonly CheckBox _crossed = new() { Text = Tr.T("Lyres croisées (gauche et droite en miroir)"), AutoSize = true };
    private readonly CheckBox _spiderMirror = new() { Text = Tr.T("Spider inversé (ailes en miroir)"), AutoSize = true };
    private readonly TableLayoutPanel _fixtureList = Ui.Table(3);

    // Right: automatic sweep and tempo
    private readonly CheckBox _run = new() { Text = Tr.T("Balayage auto"), Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, Font = Ui.BoldFont, Size = new Size(260, 40) };
    private readonly CheckBox _modeDelay = new() { Text = Tr.T("Délai"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly CheckBox _modeBpm = new() { Text = Tr.T("Tempo"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly CheckBox _modeMic = new() { Text = Tr.T("Tempo du micro"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly NumericUpDown _delay = new() { Minimum = 0.2m, Maximum = 60, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };
    private readonly NumericUpDown _bpm = new() { Minimum = 30, Maximum = 300, DecimalPlaces = 1, Increment = 1, Width = 70 };
    private readonly NumericUpDown _beats = new() { Minimum = 0.25m, Maximum = 64, DecimalPlaces = 2, Increment = 0.25m, Width = 70 };
    private readonly NumericUpDown _amplitude = new() { Minimum = 5, Maximum = 100, Increment = 5, Width = 70 };
    private readonly CheckBox _micListen = new() { Text = Tr.T("Micro"), Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(110, 28) };
    private readonly Label _micBpm = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 8, 3, 3) };

    private bool _updating;

    /// <summary>Builds the tab from the patch (moving heads = fixtures with pan, Spiders = fixtures with motors).</summary>
    public SweepPanel(ShowContext ctx)
    {
        _ctx = ctx;
        Dock = DockStyle.Fill;
        var settings = ctx.Show.Settings.GroupSweep;

        // ---------------- Left two-thirds: pages + pad, dimmers, options, fixtures ----------------
        _headsPage.Tag = false;
        _allPage.Tag = true;
        _pages.TabPages.Add(_headsPage);
        _pages.TabPages.Add(_allPage);
        _pages.SelectedTab = settings.IncludeSpider ? _allPage : _headsPage;
        _pages.SelectedTab.Controls.Add(_content);
        _pages.SelectedIndexChanged += (_, _) => OnPageChanged();

        var padColumn = Ui.Table(1);
        padColumn.Dock = DockStyle.None;
        padColumn.Controls.Add(_pad);
        var center = new Button { Text = Tr.T("Centrer"), AutoSize = true };
        center.Click += (_, _) => CenterAll();
        padColumn.Controls.Add(Ui.Flow(0, center, Ui.Text(Tr.T("Glisse dans le carré : balayage à la main."), color: Color.DimGray)));

        Control Fader(VerticalFader fader, string label)
        {
            var column = Ui.Table(1);
            column.Dock = DockStyle.None;
            column.Controls.Add(fader);
            column.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Top });
            return column;
        }
        _wingsColumn = Fader(_dimWings, Tr.T("Spider"));
        var faders = Ui.Flow(0, Fader(_dimAll, Tr.T("Tous")), Fader(_dimHeads, Tr.T("Lyres")), _wingsColumn);
        _tips.SetToolTip(_dimAll, Tr.T("Dimmer commun : toutes les lyres (et le Spider sur la page « Lyres + Spider »)."));
        _tips.SetToolTip(_dimHeads, Tr.T("Dimmer des lyres."));
        _tips.SetToolTip(_dimWings, Tr.T("Dimmer du Spider."));

        var top = Ui.Flow(0, padColumn, Ui.Group(Tr.T("Dimmers"), faders));
        var options = Ui.Table(1);
        options.Dock = DockStyle.None;
        options.Controls.Add(_crossed);
        options.Controls.Add(_spiderMirror);
        _tips.SetToolTip(_crossed, Tr.T("Coché : les lyres de gauche partent à gauche quand celles de droite partent à droite (elles s'écartent puis reviennent au milieu). Décoché : toutes dans le même sens."));
        _tips.SetToolTip(_spiderMirror, Tr.T("Coché : les 2 ailes du Spider s'écartent et se rejoignent. Décoché : elles vont dans le même sens."));

        var page = Ui.Table(1);
        page.Padding = new Padding(6);
        page.Controls.Add(top);
        page.Controls.Add(Ui.Group(Tr.T("Mode"), options));
        page.Controls.Add(Ui.Group(Tr.T("Appareils pilotés et place sur scène"), _fixtureList));
        _content.Controls.Add(page);

        // ---------------- Right third: automatic sweep and tempo ----------------
        var auto = Ui.Table(3);
        auto.Dock = DockStyle.None;
        void Row(Control first, Control? second = null, string unit = "")
        {
            int row = auto.RowCount++;
            auto.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            auto.Controls.Add(first, 0, row);
            if (second is null)
            {
                auto.SetColumnSpan(first, 3);
                return;
            }
            auto.Controls.Add(second, 1, row);
            if (unit.Length > 0)
            {
                auto.Controls.Add(Ui.Text(unit), 2, row);
            }
        }
        Row(_run);
        Row(_modeDelay, _delay, Tr.T("s"));
        Row(_modeBpm, _bpm, Tr.T("BPM"));
        Row(_modeMic, _micListen);
        Row(_micBpm);
        Row(Ui.Text(Tr.T("1 aller (tempo) =")), _beats, Tr.T("temps"));
        Row(Ui.Text(Tr.T("Amplitude")), _amplitude, Tr.T("%"));
        auto.Controls.Add(new Label
        {
            Text = Tr.T("Le balayage auto va à gauche, attend, va à droite, attend… Amplitude 100 % = ± un quart de la course du pan des lyres, toute la course des ailes du Spider."),
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 10, 3, 3),
        });
        auto.SetColumnSpan(auto.Controls[^1], 3);
        _tips.SetToolTip(_modeDelay, Tr.T("Un aller toutes les N secondes."));
        _tips.SetToolTip(_modeBpm, Tr.T("Tempo saisi en BPM (le même que l'onglet Soirée / tap tempo) : 1 aller = N temps."));
        _tips.SetToolTip(_modeMic, Tr.T("Le BPM suit le tempo détecté au micro, et le balayage se cale dessus."));
        _micListen.Enabled = Audio.MicController.DeviceNames().Count > 0;

        var right = Ui.Table(1);
        right.Padding = new Padding(6);
        right.Controls.Add(Ui.Group(Tr.T("Balayage auto et tempo"), auto));
        var rightScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        rightScroll.Controls.Add(right);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66.7f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        layout.Controls.Add(_pages, 0, 0);
        layout.Controls.Add(rightScroll, 1, 0);
        Controls.Add(layout);

        // ---------------- Values and events ----------------
        _updating = true;
        _crossed.Checked = settings.Crossed;
        _spiderMirror.Checked = settings.SpiderMirror;
        Ui.SetValue(_delay, settings.DelaySeconds);
        Ui.SetValue(_beats, settings.Beats);
        Ui.SetValue(_amplitude, settings.AmplitudePercent);
        Ui.SetValue(_bpm, ctx.Engine.Bpm);
        _run.Checked = ctx.Engine.IsGroupSweeping;
        _updating = false;

        _pad.ValueChanged += (_, _) => OnPad();
        _dimAll.ValueChanged += (_, _) => OnDimmer(heads: true, wings: IncludeSpider, _dimAll.Value);
        _dimHeads.ValueChanged += (_, _) => OnDimmer(heads: true, wings: false, _dimHeads.Value);
        _dimWings.ValueChanged += (_, _) => OnDimmer(heads: false, wings: true, _dimWings.Value);
        _crossed.CheckedChanged += (_, _) => SaveSettings();
        _spiderMirror.CheckedChanged += (_, _) => SaveSettings();
        _delay.ValueChanged += (_, _) => SaveSettings();
        _beats.ValueChanged += (_, _) => SaveSettings();
        _amplitude.ValueChanged += (_, _) => SaveSettings();
        _run.CheckedChanged += (_, _) => OnRunToggled();
        _modeDelay.CheckedChanged += (_, _) => OnModeChecked(Mode.Delay, _modeDelay);
        _modeBpm.CheckedChanged += (_, _) => OnModeChecked(Mode.Bpm, _modeBpm);
        _modeMic.CheckedChanged += (_, _) => OnModeChecked(Mode.Mic, _modeMic);
        _bpm.ValueChanged += (_, _) => OnBpmTyped();
        _micListen.CheckedChanged += (_, _) => OnMicListenToggled();

        RebuildFixtureList();
        ShowPage();
        ShowMode(CurrentMode());
        UpdateRunButton();
        RefreshFromEngine();

        _ctx.Mic.Changed += UpdateMicUi;
        _ctx.LiveStateChanged += RefreshFromEngine;
        UpdateMicUi();
    }

    /// <summary>true on the "Moving heads + Spider" page.</summary>
    private bool IncludeSpider => _pages.SelectedTab == _allPage;

    /// <summary>Driven moving heads and fixtures with wings of the patch (enabled only).</summary>
    private IEnumerable<(FixtureInstance Fixture, FixtureProfile Profile)> Movers() =>
        _ctx.Show.Fixtures
            .Select(f => (Fixture: f, Profile: _ctx.ProfileOf(f)))
            .Where(x => x.Profile is not null && x.Fixture.Enabled && (ShowEngine.IsSweepHead(x.Profile) || ShowEngine.IsSweepWings(x.Profile)))
            .Select(x => (x.Fixture, x.Profile!));

    /// <summary>One row per moving head / Spider: "driven" box, name and stage side (left / middle / right).</summary>
    private void RebuildFixtureList()
    {
        _fixtureList.SuspendLayout();
        foreach (Control old in _fixtureList.Controls.Cast<Control>().ToList())
        {
            old.Dispose();
        }
        _fixtureList.Controls.Clear();
        foreach (var (fixture, profile) in Movers())
        {
            var include = new CheckBox
            {
                Text = Tr.T(fixture.Name) + (ShowEngine.IsSweepWings(profile) ? Tr.T("  (Spider)") : Tr.T("  (lyre)")),
                AutoSize = true,
                Checked = fixture.InGroupSweep,
                Margin = new Padding(3, 6, 12, 3),
            };
            include.CheckedChanged += (_, _) =>
            {
                _ctx.Engine.EditShow(_ => fixture.InGroupSweep = include.Checked);
                _ctx.MarkDirty();
            };
            var side = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            side.Items.AddRange(new object[] { Tr.T("Gauche"), Tr.T("Milieu"), Tr.T("Droite") });
            side.SelectedIndex = fixture.StageSide switch { StageSide.Left => 0, StageSide.Right => 2, _ => 1 };
            side.SelectedIndexChanged += (_, _) =>
            {
                var value = side.SelectedIndex switch { 0 => StageSide.Left, 2 => StageSide.Right, _ => StageSide.Center };
                _ctx.Engine.EditShow(_ => fixture.StageSide = value);
                _ctx.MarkDirty();
            };
            _tips.SetToolTip(side, Tr.T("Place sur scène, vue du public : utilisée par le mode « croisé »."));
            int row = _fixtureList.RowCount++;
            _fixtureList.Controls.Add(include, 0, row);
            _fixtureList.Controls.Add(side, 1, row);
        }
        if (_fixtureList.Controls.Count == 0)
        {
            _fixtureList.Controls.Add(Ui.Text(Tr.T("Aucune lyre ni Spider actif dans le patch."), color: Color.DimGray));
        }
        _fixtureList.ResumeLayout();
    }

    /// <summary>Page changed: moves the shared content into it and saves whether the Spider takes part.</summary>
    private void OnPageChanged()
    {
        _pages.SelectedTab?.Controls.Add(_content);
        if (!_updating)
        {
            bool include = IncludeSpider;
            _ctx.Engine.EditShow(_ => _ctx.Show.Settings.GroupSweep.IncludeSpider = include);
            _ctx.MarkDirty();
        }
        ShowPage();
    }

    /// <summary>The Spider controls are shown only on the "Moving heads + Spider" page.</summary>
    private void ShowPage()
    {
        _wingsColumn.Visible = IncludeSpider;
        _spiderMirror.Visible = IncludeSpider;
    }

    /// <summary>Pad moved: position of every driven fixture (X) and tilt of the moving heads (Y).</summary>
    private void OnPad()
    {
        if (_updating)
        {
            return;
        }
        double x = (_pad.ValueX - 32767.5) / 32767.5;
        _ctx.Engine.SetGroupPosition(x, _pad.ValueY);
        _ctx.NotifyLiveStateChanged();
    }

    /// <summary>"Center" button: every driven fixture back to the middle (pad X centred, tilt kept).</summary>
    private void CenterAll()
    {
        _updating = true;
        _pad.ValueX = 32768;
        _updating = false;
        _ctx.Engine.SetGroupPosition(0, null);
        _ctx.NotifyLiveStateChanged();
    }

    /// <summary>Dimmer fader moved: sets the dimmers of the driven moving heads and / or Spiders.</summary>
    private void OnDimmer(bool heads, bool wings, int value)
    {
        if (_updating)
        {
            return;
        }
        _ctx.Engine.SetGroupDimmer(heads, wings, (byte)Math.Clamp(value, 0, 255));
        _ctx.NotifyLiveStateChanged();
    }

    /// <summary>Copies the boxes into the configuration settings (saved with it).</summary>
    private void SaveSettings()
    {
        if (_updating)
        {
            return;
        }
        bool crossed = _crossed.Checked;
        bool mirror = _spiderMirror.Checked;
        double delay = (double)_delay.Value;
        double beats = (double)_beats.Value;
        int amplitude = (int)_amplitude.Value;
        _ctx.Engine.EditShow(_ =>
        {
            var g = _ctx.Show.Settings.GroupSweep;
            g.Crossed = crossed;
            g.SpiderMirror = mirror;
            g.DelaySeconds = delay;
            g.Beats = beats;
            g.AmplitudePercent = amplitude;
        });
        _ctx.MarkDirty();
    }

    /// <summary>"Automatic sweep" button: starts or stops it in the engine.</summary>
    private void OnRunToggled()
    {
        if (_updating)
        {
            return;
        }
        _ctx.Engine.SetGroupSweep(_run.Checked);
        UpdateRunButton();
        _ctx.Status(_run.Checked ? Tr.T("Balayage auto lancé.") : Tr.T("Balayage auto arrêté."));
    }

    /// <summary>Colours the automatic sweep button according to its state.</summary>
    private void UpdateRunButton()
    {
        _run.BackColor = _run.Checked ? Color.FromArgb(255, 210, 60) : SystemColors.Control;
        _run.UseVisualStyleBackColor = !_run.Checked;
    }

    /// <summary>Current rate source, from the settings (locked to the tempo or not) and the microphone (the BPM follows it or not).</summary>
    private Mode CurrentMode() =>
        !_ctx.Show.Settings.GroupSweep.SyncToBpm ? Mode.Delay
        : _ctx.Mic.FollowBpm ? Mode.Mic
        : Mode.Bpm;

    /// <summary>A mode box was ticked: the others are unticked; unticking the active box ticks it again.</summary>
    private void OnModeChecked(Mode mode, CheckBox box)
    {
        if (_updating)
        {
            return;
        }
        if (!box.Checked)
        {
            ShowMode(CurrentMode()); // there must always be a mode
            return;
        }
        if (mode == Mode.Mic && !_ctx.Mic.IsListening)
        {
            _micListen.Checked = true; // -> OnMicListenToggled: switches the microphone on
        }
        _ctx.Mic.FollowBpm = mode == Mode.Mic;
        _ctx.Engine.EditShow(_ => _ctx.Show.Settings.GroupSweep.SyncToBpm = mode != Mode.Delay);
        _ctx.MarkDirty();
        ShowMode(mode);
    }

    /// <summary>Ticks the box of <paramref name="mode"/> and enables only the settings it uses.</summary>
    private void ShowMode(Mode mode)
    {
        _updating = true;
        try
        {
            _modeDelay.Checked = mode == Mode.Delay;
            _modeBpm.Checked = mode == Mode.Bpm;
            _modeMic.Checked = mode == Mode.Mic;
        }
        finally
        {
            _updating = false;
        }
        _delay.Enabled = mode == Mode.Delay;
        _bpm.Enabled = mode == Mode.Bpm;
        _beats.Enabled = mode != Mode.Delay;
    }

    /// <summary>Typed BPM: becomes the global BPM (Show tab).</summary>
    private void OnBpmTyped()
    {
        if (_updating)
        {
            return;
        }
        _ctx.Engine.Bpm = (double)_bpm.Value;
        _ctx.Show.Settings.Bpm = _ctx.Engine.Bpm;
        _ctx.NotifyLiveStateChanged();
    }

    /// <summary>"Mic" button: starts or stops the shared microphone.</summary>
    private void OnMicListenToggled()
    {
        if (_updating)
        {
            return;
        }
        if (!_micListen.Checked)
        {
            _ctx.Mic.Stop();
            return;
        }
        try
        {
            _ctx.Mic.Start();
            _ctx.Status(Tr.T("Micro : écoute en cours (le tempo apparaît après quelques secondes de musique)."));
        }
        catch (Exception ex)
        {
            _ctx.Mic.Stop();
            _ctx.Status(Tr.F("Impossible d'ouvrir le micro : {0}", ex.Message));
        }
    }

    /// <summary>Microphone state and BPM -> controls (called by the microphone 10 times/s and on every change).</summary>
    private void UpdateMicUi()
    {
        if (IsDisposed)
        {
            return;
        }
        var mic = _ctx.Mic;
        _updating = true;
        try
        {
            _micListen.Checked = mic.IsListening;
            if (!_bpm.Focused)
            {
                Ui.SetValue(_bpm, _ctx.Engine.Bpm);
            }
        }
        finally
        {
            _updating = false;
        }
        ShowMode(CurrentMode());
        _micListen.Text = mic.IsListening ? Tr.T("Micro : ON") : Tr.T("Micro");
        _micListen.BackColor = mic.IsListening ? Color.FromArgb(120, 200, 120) : SystemColors.Control;
        _micListen.UseVisualStyleBackColor = !mic.IsListening;
        double bpm = mic.Bpm;
        _micBpm.Text = !mic.IsListening ? Tr.F("BPM : {0:0.0}", _ctx.Engine.Bpm)
            : bpm > 0 ? Tr.F("Tempo micro : {0:0.0} BPM", bpm) : Tr.T("Tempo : écoute…");
        _micBpm.ForeColor = mic.IsListening && bpm > 0 ? Ui.OkColor : mic.IsListening ? Ui.WarningColor : Color.DimGray;
    }

    /// <summary>Dimmer faders and run button realigned on the engine (scene recalled, other tab...).</summary>
    public void RefreshFromEngine()
    {
        if (IsDisposed)
        {
            return;
        }
        byte heads = 0, wings = 0;
        int tilt = -1;
        foreach (var (fixture, profile) in Movers().Where(m => m.Fixture.InGroupSweep))
        {
            byte[] values = _ctx.Engine.GetValues(fixture.Id);
            int dimmer = profile.IndexOf(ChannelType.Dimmer);
            byte level = dimmer >= 0 && dimmer < values.Length ? values[dimmer] : (byte)0;
            if (ShowEngine.IsSweepHead(profile))
            {
                heads = Math.Max(heads, level);
                if (tilt < 0)
                {
                    tilt = ShowEngine.GetAxis16(profile, values, ChannelType.Tilt, ChannelType.TiltFine);
                }
            }
            else
            {
                wings = Math.Max(wings, level);
            }
        }
        _updating = true;
        try
        {
            if (!_dimHeads.Capture)
            {
                _dimHeads.Value = heads;
            }
            if (!_dimWings.Capture)
            {
                _dimWings.Value = wings;
            }
            if (!_dimAll.Capture)
            {
                _dimAll.Value = IncludeSpider ? Math.Max(heads, wings) : heads;
            }
            if (tilt >= 0 && !_pad.Capture)
            {
                _pad.ValueY = tilt;
            }
            _run.Checked = _ctx.Engine.IsGroupSweeping;
        }
        finally
        {
            _updating = false;
        }
        UpdateRunButton();
    }

    /// <summary>Unsubscribes from the microphone and the context before the tab is destroyed.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ctx.Mic.Changed -= UpdateMicUi;
            _ctx.LiveStateChanged -= RefreshFromEngine;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
