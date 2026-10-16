// =====================================================================================
//  BeatEffectBlock.cs
// -------------------------------------------------------------------------------------
//  Bloc "Effet au tempo" des projecteurs sans moteur (PAR UV, PAR RGB...) :
//    - bouton marche / arret ;
//    - effet : chenillard des groupes de LED (UV 1 -> 2 -> 3...) ou pulsation ;
//    - 3 modes qui s'excluent pour la cadence, comme l'effet gauche/droite du Spider :
//        Delai (s)  |  Tempo (BPM saisi = BPM general)  |  Tempo du micro (auto) ;
//    - "1 pas = N temps" en modes tempo.
//  Les reglages sont enregistres avec l'appareil (FixtureInstance.Beat*).
// =====================================================================================

using System.ComponentModel;
using DmxController.Core.Model;

namespace DmxController.UI;

[DesignerCategory("Code")]
internal sealed class BeatEffectBlock : TableLayoutPanel
{
    private enum Mode { Delay, Bpm, Mic }

    private sealed record KindItem(BeatEffectKind Kind, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly ShowContext _ctx;
    private readonly FixtureInstance _fixture;
    private readonly CheckBox _run = new() { Text = Tr.T("Effet au tempo"), Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, Font = Ui.BoldFont, Size = new Size(260, 32) };
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, Margin = new Padding(3, 4, 3, 3) };
    private readonly CheckBox _modeDelay = new() { Text = Tr.T("Délai"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly CheckBox _modeBpm = new() { Text = Tr.T("Tempo"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly CheckBox _modeMic = new() { Text = Tr.T("Tempo du micro"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly NumericUpDown _delay = new() { Minimum = 0.05m, Maximum = 60, DecimalPlaces = 2, Increment = 0.05m, Width = 70 };
    private readonly NumericUpDown _bpm = new() { Minimum = 30, Maximum = 300, DecimalPlaces = 1, Increment = 1, Width = 70 };
    private readonly NumericUpDown _beats = new() { Minimum = 0.25m, Maximum = 64, DecimalPlaces = 2, Increment = 0.25m, Width = 70 };
    private readonly CheckBox _micListen = new() { Text = Tr.T("Micro"), Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(110, 28) };
    private readonly Label _micBpm = new() { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(3, 8, 3, 3) };
    private readonly ToolTip _tips = new();
    private bool _updating;

    public BeatEffectBlock(ShowContext ctx, FixtureInstance fixture, FixtureProfile profile)
    {
        _ctx = ctx;
        _fixture = fixture;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 3;
        GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        for (int c = 0; c < 3; c++)
        {
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        int groups = profile.Channels.Count(c => ChannelTypeInfo.IsColorMix(c.Type));
        _kind.Items.Add(new KindItem(BeatEffectKind.Chase, Tr.F("Chenillard ({0} groupes)", groups)));
        _kind.Items.Add(new KindItem(BeatEffectKind.Pulse, Tr.T("Pulsation (allumé / éteint)")));
        _kind.SelectedIndex = fixture.BeatEffect == BeatEffectKind.Pulse ? 1 : 0;

        Ui.SetValue(_delay, fixture.BeatDelaySeconds);
        Ui.SetValue(_beats, fixture.BeatBeats);
        Ui.SetValue(_bpm, ctx.Engine.Bpm);
        _run.Checked = ctx.Engine.IsBeatEffectRunning(fixture.Id);
        _micListen.Enabled = Audio.MicController.DeviceNames().Count > 0;

        _run.CheckedChanged += (_, _) => OnRunToggled();
        _kind.SelectionChangeCommitted += (_, _) => Save();
        _delay.ValueChanged += (_, _) => Save();
        _beats.ValueChanged += (_, _) => Save();
        _bpm.ValueChanged += (_, _) => OnBpmTyped();
        _modeDelay.CheckedChanged += (_, _) => OnModeChecked(Mode.Delay, _modeDelay);
        _modeBpm.CheckedChanged += (_, _) => OnModeChecked(Mode.Bpm, _modeBpm);
        _modeMic.CheckedChanged += (_, _) => OnModeChecked(Mode.Mic, _modeMic);
        _micListen.CheckedChanged += (_, _) => OnMicListenToggled();

        _tips.SetToolTip(_run, Tr.T("Chenillard : les groupes de LED s'allument chacun leur tour. Pulsation : tout s'allume puis s'éteint."));
        _tips.SetToolTip(_modeDelay, Tr.T("Un pas toutes les N secondes."));
        _tips.SetToolTip(_modeBpm, Tr.T("Tempo saisi en BPM (le même que l'onglet Soirée / tap tempo) : 1 pas = N temps."));
        _tips.SetToolTip(_modeMic, Tr.T("Tempo détecté au micro (automatique) : 1 pas = N temps."));

        Row(_run);
        Row(Ui.Text(Tr.T("Effet")), _kind);
        Row(_modeDelay, _delay, Tr.T("s"));
        Row(_modeBpm, _bpm, Tr.T("BPM"));
        Row(_modeMic, _micListen);
        Row(_micBpm);
        Row(Ui.Text(Tr.T("1 pas (tempo) =")), _beats, Tr.T("temps"));

        _ctx.Mic.Changed += UpdateUi;
        ShowMode(CurrentMode());
        UpdateUi();
        UpdateRunButton();
    }

    private void Row(Control first, Control? second = null, string unit = "")
    {
        int row = RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(first, 0, row);
        if (second is null)
        {
            SetColumnSpan(first, 3);
            return;
        }
        Controls.Add(second, 1, row);
        if (unit.Length > 0)
        {
            Controls.Add(Ui.Text(unit), 2, row);
        }
        else
        {
            SetColumnSpan(second, 2);
        }
    }

    // ---------------------------------------------------------------------------------

    private void OnRunToggled()
    {
        _ctx.Engine.SetBeatEffect(_fixture.Id, _run.Checked);
        UpdateRunButton();
        _ctx.Status(_run.Checked ? Tr.F("{0} : effet au tempo lancé.", _fixture.Name) : Tr.F("{0} : effet au tempo arrêté.", _fixture.Name));
    }

    /// <summary>Arrete l'effet (bouton TOUT COUPER de l'onglet).</summary>
    public void Stop() => _run.Checked = false; // -> OnRunToggled

    private void UpdateRunButton()
    {
        _run.BackColor = _run.Checked ? Color.FromArgb(255, 210, 60) : SystemColors.Control;
        _run.UseVisualStyleBackColor = !_run.Checked;
    }

    private void Save()
    {
        var kind = (_kind.SelectedItem as KindItem)?.Kind ?? BeatEffectKind.Chase;
        double delay = (double)_delay.Value;
        double beats = (double)_beats.Value;
        _ctx.Engine.EditShow(_ =>
        {
            _fixture.BeatEffect = kind;
            _fixture.BeatDelaySeconds = delay;
            _fixture.BeatBeats = beats;
        });
        _ctx.MarkDirty();
    }

    private Mode CurrentMode() =>
        !_fixture.BeatSyncToBpm ? Mode.Delay
        : _ctx.Mic.FollowBpm ? Mode.Mic
        : Mode.Bpm;

    /// <summary>Une case de mode cochee : les autres se decochent ; decocher la case active la recoche.</summary>
    private void OnModeChecked(Mode mode, CheckBox box)
    {
        if (_updating)
        {
            return;
        }
        if (!box.Checked)
        {
            ShowMode(CurrentMode());
            return;
        }
        if (mode == Mode.Mic && !_ctx.Mic.IsListening)
        {
            _micListen.Checked = true; // -> allume le micro
        }
        _ctx.Mic.FollowBpm = mode == Mode.Mic;
        _ctx.Engine.EditShow(_ => _fixture.BeatSyncToBpm = mode != Mode.Delay);
        _ctx.MarkDirty();
        ShowMode(mode);
    }

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

    /// <summary>Etat du micro et BPM -> controles (appele par le micro et par l'onglet).</summary>
    public void UpdateUi()
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ctx.Mic.Changed -= UpdateUi;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
