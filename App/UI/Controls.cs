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

/// <summary>Pad 2D : X = pan, Y = tilt, en valeurs 16 bits (0..65535).</summary>
[DesignerCategory("Code")]
internal sealed class XYPad : Control
{
    private int _x = 32768;
    private int _y = 32768;

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

        TextRenderer.DrawText(g, "Pan →", Font, new Point(r.Left + 4, r.Bottom - 20), Color.Silver);
        TextRenderer.DrawText(g, "Tilt ↓", Font, new Point(r.Left + 4, r.Top + 2), Color.Silver);
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
