// =====================================================================================
//  Controls.cs
// -------------------------------------------------------------------------------------
//  Hand-drawn controls:
//    - XYPad         : square where you click/drag to aim a moving head (pan/tilt)
//    - VerticalFader : console-style vertical fader
//    - GroupLevelPad : bar chart of the LED groups of a fixture (A, B, C, All)
//    - DmxMonitor    : bars showing the values actually sent to the box
// =====================================================================================

using System.ComponentModel;
using System.Drawing.Drawing2D;
using DmxController.Core.Protocol;

namespace DmxController.UI;

/// <summary>2D pad with 16-bit values (0..65535): pan/tilt for a moving head, wings/dimmer for the Spider.</summary>
[DesignerCategory("Code")]
internal sealed class XYPad : Control
{
    private int _x = 32768;
    private int _y = 32768;

    /// <summary>Label of the horizontal axis (drawn bottom left).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string XLabel { get; set; } = Tr.T("Pan →");

    /// <summary>Label of the vertical axis (drawn top left).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string YLabel { get; set; } = Tr.T("Tilt ↓");

    /// <summary>Raised when the user moves the point (not on a change made by code).</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Creates the pad (double-buffered, centred point).</summary>
    public XYPad()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.FromArgb(28, 30, 36);
        Cursor = Cursors.Cross;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ValueX
    {
        get => _x;
        set
        {
            _x = Math.Clamp(value, 0, 65535);
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ValueY
    {
        get => _y;
        set
        {
            _y = Math.Clamp(value, 0, 65535);
            Invalidate();
        }
    }

    private Rectangle Inner => new(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - 16));

    /// <summary>Click: moves the point under the mouse.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Capture = true;
            SetFromPoint(e.Location);
        }
    }

    /// <summary>Drag with the left button: the point follows the mouse.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left)
        {
            SetFromPoint(e.Location);
        }
    }

    /// <summary>Converts a mouse position into 16-bit X/Y values and raises ValueChanged.</summary>
    private void SetFromPoint(Point p)
    {
        var r = Inner;
        _x = (int)Math.Round(Math.Clamp((p.X - r.Left) / (double)r.Width, 0, 1) * 65535);
        _y = (int)Math.Round(Math.Clamp((p.Y - r.Top) / (double)r.Height, 0, 1) * 65535);
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Draws the grid, the axis labels and the current point.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = Inner;

        using var grid = new Pen(Color.FromArgb(60, 64, 78));
        for (int k = 1; k < 4; k++)
        {
            int x = r.Left + r.Width * k / 4;
            int y = r.Top + r.Height * k / 4;
            g.DrawLine(grid, x, r.Top, x, r.Bottom);
            g.DrawLine(grid, r.Left, y, r.Right, y);
        }
        using var border = new Pen(Color.FromArgb(110, 118, 140));
        g.DrawRectangle(border, r);

        float px = r.Left + _x / 65535f * r.Width;
        float py = r.Top + _y / 65535f * r.Height;
        using var cross = new Pen(Color.FromArgb(150, 255, 170, 60)) { DashStyle = DashStyle.Dot };
        g.DrawLine(cross, px, r.Top, px, r.Bottom);
        g.DrawLine(cross, r.Left, py, r.Right, py);
        using var dot = new SolidBrush(Color.Orange);
        g.FillEllipse(dot, px - 8, py - 8, 16, 16);

        TextRenderer.DrawText(g, XLabel, Font, new Point(r.Left + 4, r.Bottom - 20), Color.Silver);
        TextRenderer.DrawText(g, YLabel, Font, new Point(r.Left + 4, r.Top + 2), Color.Silver);
    }
}

/// <summary>
/// Console-style vertical fader: bottom = 0 %, top = 100 % (value 0..255). Click or drag
/// anywhere in the area; the mouse wheel moves it in 5 % steps.
/// </summary>
[DesignerCategory("Code")]
internal sealed class VerticalFader : Control
{
    private int _value;

    /// <summary>Raised when the user changes the value (not on a change made by code).</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Creates the fader (double-buffered, value 0).</summary>
    public VerticalFader()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(28, 30, 36);
        Cursor = Cursors.SizeNS;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 255);
            Invalidate();
        }
    }

    /// <summary>Fill colour (e.g. warm white for LEDs).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.FromArgb(255, 200, 80);

    private Rectangle Track => new(6, 6, Math.Max(1, Width - 12), Math.Max(1, Height - 30));

    /// <summary>Click: sets the value at the mouse height.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            Capture = true;
            SetFromY(e.Y);
        }
    }

    /// <summary>Drag with the left button: the value follows the mouse height.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left)
        {
            SetFromY(e.Y);
        }
    }

    /// <summary>Mouse wheel: +/- 5 % per notch.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Change(_value + Math.Sign(e.Delta) * 13); // ~5 %
    }

    /// <summary>Converts a mouse height into a value (bottom = 0, top = 255).</summary>
    private void SetFromY(int y)
    {
        var r = Track;
        double fraction = 1 - Math.Clamp((y - r.Top) / (double)r.Height, 0, 1); // bottom = 0, top = 1
        Change((int)Math.Round(fraction * 255));
    }

    /// <summary>Sets the value (clamped to 0..255) and raises ValueChanged when it changed.</summary>
    private void Change(int value)
    {
        value = Math.Clamp(value, 0, 255);
        if (value == _value)
        {
            return;
        }
        _value = value;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Draws the background, the filled part up to the level and the knob mark.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = Track;
        using var empty = new SolidBrush(Color.FromArgb(48, 52, 64));
        g.FillRectangle(empty, r);

        int h = (int)Math.Round(_value / 255.0 * r.Height);
        using var fill = new SolidBrush(FillColor);
        g.FillRectangle(fill, r.Left, r.Bottom - h, r.Width, h);

        using var border = new Pen(Color.FromArgb(110, 118, 140));
        g.DrawRectangle(border, r);

        // Small horizontal mark at the current level (the fader "knob").
        using var knob = new Pen(Color.White, 3);
        g.DrawLine(knob, r.Left - 3, r.Bottom - h, r.Right + 3, r.Bottom - h);

        string text = Tr.F("{0} %", (_value * 100 / 255));
        TextRenderer.DrawText(g, text, Font, new Rectangle(0, Height - 22, Width, 20), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>
/// Chart of the LED groups of a fixture (UV PAR: groups A, B, C) + a last "All" column.
/// X: the column = the controlled group; Y: its level (bottom = 0 %, top = 100 %).
/// Click or drag: the group under the mouse takes the level of the pointer height.
/// </summary>
[DesignerCategory("Code")]
internal sealed class GroupLevelPad : Control
{
    private byte[] _levels = Array.Empty<byte>();
    private int _active = -1;

    /// <summary>Raised when the user sets a column: (column index, level 0..255). Last column = all.</summary>
    public event Action<int, byte>? LevelChanged;

    /// <summary>Creates the chart (double-buffered).</summary>
    public GroupLevelPad()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.FromArgb(28, 30, 36);
        Cursor = Cursors.Hand;
    }

    /// <summary>Group names (A, B, C...); an "All" column is added at the end.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] GroupNames { get; set; } = { "A", "B", "C" };

    /// <summary>Current levels of the groups (display only, no event raised).</summary>
    public void SetLevels(byte[] levels)
    {
        _levels = levels;
        Invalidate();
    }

    private int Columns => GroupNames.Length + 1;

    private Rectangle Area => new(6, 6, Math.Max(1, Width - 12), Math.Max(1, Height - 28));

    /// <summary>Click: the column under the mouse takes the level of the pointer height.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Capture = true;
            _active = ColumnAt(e.X);
            Apply(e.Y);
        }
    }

    /// <summary>Drag: same as a click, the column under the mouse can change while dragging.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left)
        {
            _active = ColumnAt(e.X); // you can drag from one group to the next
            Apply(e.Y);
        }
    }

    /// <summary>Releases the mouse capture.</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _active = -1;
        Invalidate();
    }

    /// <summary>Index of the column under horizontal position <paramref name="x"/>.</summary>
    private int ColumnAt(int x)
    {
        var r = Area;
        return Math.Clamp((int)((x - r.Left) / (double)r.Width * Columns), 0, Columns - 1);
    }

    /// <summary>Converts the pointer height into a level and raises LevelChanged for the active column.</summary>
    private void Apply(int y)
    {
        var r = Area;
        double fraction = 1 - Math.Clamp((y - r.Top) / (double)r.Height, 0, 1); // top = 100 %
        LevelChanged?.Invoke(_active, (byte)Math.Round(fraction * 255));
        Invalidate();
    }

    /// <summary>Draws one bar per group plus the "All" column, with their percentages.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = Area;
        float columnWidth = r.Width / (float)Columns;
        using var grid = new Pen(Color.FromArgb(70, 74, 88));
        using var uv = new SolidBrush(Color.FromArgb(140, 70, 230));
        using var all = new SolidBrush(Color.FromArgb(190, 150, 255));
        using var highlight = new SolidBrush(Color.FromArgb(40, 255, 255, 255));

        for (int c = 0; c < Columns; c++)
        {
            float x = r.Left + c * columnWidth;
            if (c == _active)
            {
                g.FillRectangle(highlight, x, r.Top, columnWidth, r.Height);
            }

            // Level: the group's own, or the highest of the groups for "All".
            bool isAll = c == Columns - 1;
            int level = isAll ? (_levels.Length == 0 ? 0 : _levels.Max()) : (c < _levels.Length ? _levels[c] : 0);
            float h = level / 255f * r.Height;
            g.FillRectangle(isAll ? all : uv, x + 8, r.Bottom - h, columnWidth - 16, h);

            if (c > 0)
            {
                g.DrawLine(grid, x, r.Top, x, r.Bottom);
            }
            string name = isAll ? Tr.T("Tous") : GroupNames[c];
            TextRenderer.DrawText(g, Tr.F("{0}  {1} %", name, (level * 100 / 255)), Font, new Rectangle((int)x, Height - 22, (int)columnWidth, 20),
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        // 50 % mark and border.
        using var half = new Pen(Color.FromArgb(90, 94, 110)) { DashStyle = DashStyle.Dot };
        g.DrawLine(half, r.Left, r.Top + r.Height / 2, r.Right, r.Top + r.Height / 2);
        using var border = new Pen(Color.FromArgb(110, 118, 140));
        g.DrawRectangle(border, r);
    }
}

/// <summary>Output monitor: one bar per DMX channel.</summary>
[DesignerCategory("Code")]
internal sealed class DmxMonitor : Control
{
    private readonly byte[] _values = DmxFrame.NewUniverse();
    private int _count = 48;

    /// <summary>Creates the monitor (double-buffered).</summary>
    public DmxMonitor()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.FromArgb(24, 26, 32);
    }

    /// <summary>Number of channels shown (from channel 1).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ChannelCount
    {
        get => _count;
        set
        {
            _count = Math.Clamp(value, 8, DmxFrame.UniverseSize);
            Invalidate();
        }
    }

    /// <summary>Shows a new universe (513 bytes, index N = channel N).</summary>
    public void SetValues(byte[] universe)
    {
        Array.Copy(universe, _values, Math.Min(universe.Length, _values.Length));
        Invalidate();
    }

    /// <summary>Draws one bar per channel, with channel numbers every 8 channels.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int left = 6;
        int top = 6;
        int bottom = Height - 20;
        float width = (Width - 12f) / _count;
        using var bar = new SolidBrush(Color.FromArgb(255, 170, 50));
        using var empty = new SolidBrush(Color.FromArgb(44, 47, 58));

        for (int ch = 1; ch <= _count; ch++)
        {
            float x = left + (ch - 1) * width;
            float full = bottom - top;
            float h = _values[ch] / 255f * full;
            g.FillRectangle(empty, x + 1, top, Math.Max(1, width - 2), full);
            g.FillRectangle(bar, x + 1, bottom - h, Math.Max(1, width - 2), h);

            if (ch == 1 || ch % 8 == 0)
            {
                TextRenderer.DrawText(g, ch.ToString(), Font, new Point((int)x, bottom + 2), Color.Silver);
            }
        }
    }
}
