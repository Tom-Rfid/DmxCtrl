// =====================================================================================
//  Controls.cs
// -------------------------------------------------------------------------------------
//  Deux controles dessines a la main :
//    - XYPad      : carre ou l'on clique/glisse pour orienter une lyre (pan/tilt)
//    - DmxMonitor : barres montrant les valeurs reellement envoyees au boitier
// =====================================================================================

using System.ComponentModel;
using System.Drawing.Drawing2D;
using DmxController.Core.Protocol;

namespace DmxController.UI;

/// <summary>Pad 2D en valeurs 16 bits (0..65535) : pan/tilt pour une lyre, ailes/dimmer pour le Spider.</summary>
[DesignerCategory("Code")]
internal sealed class XYPad : Control
{
    private int _x = 32768;
    private int _y = 32768;

    /// <summary>Libelle de l'axe horizontal (dessine en bas a gauche).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string XLabel { get; set; } = "Pan →";

    /// <summary>Libelle de l'axe vertical (dessine en haut a gauche).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string YLabel { get; set; } = "Tilt ↓";

    /// <summary>Leve quand l'utilisateur deplace le point (pas lors d'un changement par code).</summary>
    public event EventHandler? ValueChanged;

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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Capture = true;
            SetFromPoint(e.Location);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left)
        {
            SetFromPoint(e.Location);
        }
    }

    private void SetFromPoint(Point p)
    {
        var r = Inner;
        _x = (int)Math.Round(Math.Clamp((p.X - r.Left) / (double)r.Width, 0, 1) * 65535);
        _y = (int)Math.Round(Math.Clamp((p.Y - r.Top) / (double)r.Height, 0, 1) * 65535);
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

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
/// Fader vertical facon console : bas = 0 %, haut = 100 % (valeur 0..255). On clique ou on
/// glisse n'importe ou dans la zone ; la molette ajuste par pas de 5 %.
/// </summary>
[DesignerCategory("Code")]
internal sealed class VerticalFader : Control
{
    private int _value;

    /// <summary>Leve quand l'utilisateur change la valeur (pas lors d'un changement par code).</summary>
    public event EventHandler? ValueChanged;

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

    /// <summary>Couleur de remplissage (ex. blanc chaud pour des LED).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.FromArgb(255, 200, 80);

    private Rectangle Track => new(6, 6, Math.Max(1, Width - 12), Math.Max(1, Height - 30));

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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left)
        {
            SetFromY(e.Y);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Change(_value + Math.Sign(e.Delta) * 13); // ~5 %
    }

    private void SetFromY(int y)
    {
        var r = Track;
        double fraction = 1 - Math.Clamp((y - r.Top) / (double)r.Height, 0, 1); // bas = 0, haut = 1
        Change((int)Math.Round(fraction * 255));
    }

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

        // Petit repere horizontal au niveau actuel (la "manette" du fader).
        using var knob = new Pen(Color.White, 3);
        g.DrawLine(knob, r.Left - 3, r.Bottom - h, r.Right + 3, r.Bottom - h);

        string text = $"{_value * 100 / 255} %";
        TextRenderer.DrawText(g, text, Font, new Rectangle(0, Height - 22, Width, 20), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Moniteur de sortie : une barre par canal DMX.</summary>
[DesignerCategory("Code")]
internal sealed class DmxMonitor : Control
{
    private readonly byte[] _values = DmxFrame.NewUniverse();
    private int _count = 48;

    public DmxMonitor()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.FromArgb(24, 26, 32);
    }

    /// <summary>Nombre de canaux affiches (a partir du canal 1).</summary>
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

    public void SetValues(byte[] universe)
    {
        Array.Copy(universe, _values, Math.Min(universe.Length, _values.Length));
        Invalidate();
    }

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
