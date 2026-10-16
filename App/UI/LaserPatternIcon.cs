using System.Drawing.Drawing2D;

namespace DmxController.UI;

/// <summary>
/// Mini dessin d'un motif du laser Galactic (approximation a l'oeil de ce que projette l'appareil).
/// Les formes sont tracees dans un repere -1..1 (x vers la droite, y vers le bas).
/// </summary>
internal sealed class LaserPatternIcon : Control
{
    private static readonly Color Background = Color.FromArgb(28, 28, 34);
    private static readonly Color Beam = Color.FromArgb(70, 230, 90);

    private readonly int _index;

    public LaserPatternIcon(int index)
    {
        _index = index;
        Size = new Size(32, 26);
        Margin = new Padding(2);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float half = Math.Min(Width, Height) / 2f - 3;
        var state = g.Save();
        g.TranslateTransform(Width / 2f, Height / 2f);
        g.ScaleTransform(half, half);
        Draw(g, _index);
        g.Restore(state);
    }

    private static Pen Line(float width = 1.4f, bool dotted = false, bool fine = false)
    {
        // Le repere est mis a l'echelle : on exprime l'epaisseur en "pixels" / echelle approximative.
        var pen = new Pen(Beam, width / 10f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (dotted)
        {
            pen.DashStyle = DashStyle.Custom;
            pen.DashPattern = fine ? new[] { 0.6f, 1.6f } : new[] { 1.4f, 2f };
            pen.DashCap = DashCap.Round;
        }
        return pen;
    }

    private static PointF P(float x, float y) => new(x, y);

    private static PointF[] Wave(float y, float amplitude, float periods, float from = -1f, float to = 1f)
    {
        var points = new PointF[33];
        for (int i = 0; i < points.Length; i++)
        {
            float t = i / 32f;
            float x = from + (to - from) * t;
            points[i] = P(x, y - amplitude * MathF.Sin(t * periods * 2 * MathF.PI));
        }
        return points;
    }

    /// <summary>Contour d'une croix creuse, tournee de <paramref name="turn"/> radians.</summary>
    private static PointF[] Cross(float turn)
    {
        const float a = 0.3f, b = 0.9f;
        float c = MathF.Cos(turn), s = MathF.Sin(turn);
        var pts = new[] { P(-a, -b), P(a, -b), P(a, -a), P(b, -a), P(b, a), P(a, a), P(a, b), P(-a, b), P(-a, a), P(-b, a), P(-b, -a), P(-a, -a) };
        return pts.Select(q => P(q.X * c - q.Y * s, q.X * s + q.Y * c)).ToArray();
    }

    private static void Draw(Graphics g, int index)
    {
        using var pen = Line();
        switch (index + 1)
        {
            case 1: g.DrawEllipse(pen, -0.85f, -0.85f, 1.7f, 1.7f); break;
            case 2: { using var p = Line(dotted: true); g.DrawEllipse(p, -0.85f, -0.85f, 1.7f, 1.7f); break; }
            case 3: g.DrawPolygon(pen, new[] { P(0, -0.85f), P(0.9f, 0.75f), P(-0.9f, 0.75f) }); break;
            case 4: g.DrawRectangle(pen, -0.75f, -0.75f, 1.5f, 1.5f); break;
            case 5: { using var p = Line(dotted: true); g.DrawRectangle(p, -0.75f, -0.75f, 1.5f, 1.5f); break; }
            case 6:
                g.DrawRectangle(pen, -0.85f, -0.85f, 1.7f, 1.7f);
                g.DrawRectangle(pen, -0.45f, -0.45f, 0.9f, 0.9f);
                break;
            case 7: g.DrawPolygon(pen, Cross(0)); break;
            case 8:
            {
                var star = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    float r = i % 2 == 0 ? 0.95f : 0.4f;
                    float a = -MathF.PI / 2 + i * MathF.PI / 5;
                    star[i] = P(r * MathF.Cos(a), r * MathF.Sin(a));
                }
                g.DrawPolygon(pen, star);
                break;
            }
            case 9: g.DrawLines(pen, new[] { P(0.8f, -0.8f), P(-0.8f, -0.8f), P(-0.8f, 0.8f) }); break;
            case 10: g.DrawPolygon(pen, new[] { P(-0.9f, -0.75f), P(0.9f, 0.75f), P(0.9f, -0.75f), P(-0.9f, 0.75f) }); break;
            case 11:
            {
                var spiral = new PointF[60];
                for (int i = 0; i < spiral.Length; i++)
                {
                    float t = i / (float)(spiral.Length - 1);
                    float a = -t * 3 * 2 * MathF.PI;
                    spiral[i] = P(0.9f * t * MathF.Cos(a), 0.9f * t * MathF.Sin(a));
                }
                g.DrawLines(pen, spiral);
                break;
            }
            case 12:
                g.DrawArc(pen, -0.95f, -0.2f, 0.7f, 1f, -90, 180);
                g.DrawArc(pen, 0.25f, -0.2f, 0.7f, 1f, 90, 180);
                break;
            case 13: g.DrawCurve(pen, new[] { P(0, 0), P(-0.4f, -0.35f), P(-0.85f, -0.2f), P(-0.75f, 0.55f), P(0.3f, 0.6f), P(0.9f, -0.8f) }); break;
            case 14:
                g.DrawArc(pen, -0.9f, -0.9f, 1.8f, 1.8f, 180, 90);
                g.DrawArc(pen, -0.9f, -0.9f, 1.8f, 1.8f, 0, 90);
                break;
            case 15:
                g.DrawLines(pen, Wave(-0.4f, 0.3f, 2));
                g.DrawLines(pen, Wave(0.4f, 0.3f, 2));
                break;
            case 16: g.DrawLines(pen, Wave(0, 0.3f, 2)); break;
            case 17: g.DrawLines(pen, new[] { P(-0.95f, 0.7f), P(0, -0.75f), P(0.95f, 0.7f) }); break;
            case 18: g.DrawLines(pen, new[] { P(-0.9f, -0.7f), P(-0.45f, 0.7f), P(0, -0.3f), P(0.45f, 0.7f), P(0.9f, -0.7f) }); break;
            case 19:
                // E dont la barre du milieu est un aller-retour creux, ouvert cote gauche.
                g.DrawLines(pen, new[] { P(0.6f, -0.8f), P(-0.6f, -0.8f), P(-0.6f, -0.12f), P(0.45f, -0.12f), P(0.45f, 0.12f), P(-0.6f, 0.12f), P(-0.6f, 0.8f), P(0.6f, 0.8f) });
                break;
            case 20: g.DrawLine(pen, -0.9f, 0, 0.9f, 0); break;
            case 21:
                foreach (float x in new[] { -0.9f, -0.18f, 0.54f }) g.DrawLine(pen, x, 0, x + 0.36f, 0);
                break;
            case 22: g.DrawLine(pen, -0.9f, 0, 0.9f, 0); break;
            case 23:
                g.DrawLine(pen, -0.9f, -0.3f, 0.9f, -0.3f);
                g.DrawLine(pen, -0.9f, 0.3f, 0.9f, 0.3f);
                break;
            case 24:
                g.DrawLine(pen, -0.9f, 0, 0.9f, 0);
                g.DrawLine(pen, 0, -0.9f, 0, 0.9f);
                break;
            case 25:
                g.DrawLine(pen, -0.9f, 0.7f, 0, 0.7f);
                g.DrawLine(pen, 0, -0.7f, 0.9f, -0.7f);
                break;
            case 26:
                // Cercle allume seulement en diagonale (NE, NO, SO, SE).
                foreach (int a in new[] { 45, 135, 225, 315 }) g.DrawArc(pen, -0.85f, -0.85f, 1.7f, 1.7f, a - 25, 50);
                break;
            case 27:
                g.DrawRectangle(pen, -0.9f, 0.3f, 0.5f, 0.5f);
                g.DrawRectangle(pen, 0.4f, -0.8f, 0.5f, 0.5f);
                break;
            case 28:
                g.DrawRectangle(pen, -0.9f, -0.9f, 0.5f, 0.5f);
                g.DrawRectangle(pen, 0.4f, -0.9f, 0.5f, 0.5f);
                g.DrawRectangle(pen, -0.9f, 0.4f, 0.5f, 0.5f);
                g.DrawRectangle(pen, 0.4f, 0.4f, 0.5f, 0.5f);
                break;
            case 29: g.DrawEllipse(pen, 0.05f, -0.9f, 0.85f, 0.85f); break;
            case 30: { using var p = Line(dotted: true, fine: true); g.DrawLine(p, -0.9f, 0, 0.9f, 0); break; }
            case 31: { using var p = Line(dotted: true, fine: true); g.DrawArc(p, -0.6f, -0.3f, 1.2f, 1f, 180, 180); break; }
            case 32:
            {
                using var p = Line(dotted: true, fine: true);
                g.DrawPolygon(p, Cross(MathF.PI / 4));
                break;
            }
        }
    }
}
