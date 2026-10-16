// =====================================================================================
//  Ui.cs
// -------------------------------------------------------------------------------------
//  Small helpers to build the interface in code (fonts, sliders, "label / control"
//  grids, input boxes). The whole UI is built in code rather than with the visual
//  designer: fixture tabs are generated from the profiles, their content is not
//  known in advance.
//
//  NOTE Visual Studio: window classes carry [DesignerCategory("Code")] so that
//  Visual Studio opens them directly as code (no empty designer).
// =====================================================================================

using DmxController.Core.Model;

namespace DmxController.UI;

/// <summary>Helpers to build the interface in code.</summary>
internal static class Ui
{
    public static readonly Font BoldFont = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font BigFont = new("Segoe UI", 11f, FontStyle.Bold);
    public static readonly Font TitleFont = new("Segoe UI", 12f, FontStyle.Bold);
    public static readonly Color WarningColor = Color.FromArgb(176, 96, 0);
    public static readonly Color OkColor = Color.FromArgb(0, 120, 60);

    /// <summary>Auto-sized label (optionally bold and coloured).</summary>
    public static Label Text(string text, bool bold = false, Color? color = null)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        if (bold)
        {
            label.Font = BoldFont;
        }
        if (color is Color c)
        {
            label.ForeColor = c;
        }
        return label;
    }

    /// <summary>Small fixed-width label showing the value of a slider.</summary>
    public static Label ValueLabel(int width = 52) => new()
    {
        AutoSize = false,
        Width = width,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(3, 4, 3, 3),
    };

    /// <summary>Compact slider (no tick marks) with a fixed width.</summary>
    public static TrackBar Slider(int min, int max, int width) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = width,
        AutoSize = false,
        Height = 30,
        TickStyle = TickStyle.None,
        Margin = new Padding(3, 2, 3, 2),
    };

    /// <summary>Auto-sized grid: column 0 = label, next columns = controls.</summary>
    public static TableLayoutPanel Table(int columns)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = columns,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        for (int i = 0; i < columns; i++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }
        return table;
    }

    /// <summary>Adds a "label | control | extra" row to a two- or three-column table.</summary>
    public static void Row(TableLayoutPanel table, string label, Control control, Control? extra = null)
    {
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(Text(label), 0, row);
        table.Controls.Add(control, 1, row);
        if (extra is not null)
        {
            table.Controls.Add(extra, 2, row);
        }
    }

    /// <summary>Row of controls side by side (auto-sized, can wrap when maxWidth is set).</summary>
    public static FlowLayoutPanel Flow(int maxWidth = 0, params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = maxWidth > 0,
            Margin = new Padding(0),
        };
        if (maxWidth > 0)
        {
            flow.MaximumSize = new Size(maxWidth, 0);
        }
        flow.Controls.AddRange(controls);
        return flow;
    }

    /// <summary>Auto-sized group box with a bold title around <paramref name="content"/>.</summary>
    public static GroupBox Group(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 6, 8, 8),
            Margin = new Padding(6),
            Font = BoldFont,
        };
        // NO Dock here: an auto-sized GroupBox fits the size of its content; docked content
        // would instead take the width of the GroupBox, and both shrink each other
        // (groups squashed into a column of letters).
        content.Dock = DockStyle.None;
        content.Location = new Point(8, 20);
        content.Font = new Font("Segoe UI", 9f); // the title is bold, not the content
        if (content is Panel panel)
        {
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }
        group.Controls.Add(content);
        return group;
    }

    /// <summary>Colour button of the console (background in the colour, readable text).</summary>
    public static Button ColorButton(NamedColor color)
    {
        var back = Color.FromArgb(color.R, color.G, color.B);
        double luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        return new Button
        {
            Text = Tr.T(color.Name),
            BackColor = back,
            ForeColor = luminance > 140 ? Color.Black : Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(78, 40),
            Margin = new Padding(3),
            Font = BoldFont,
            UseVisualStyleBackColor = false,
        };
    }

    /// <summary>Sets a slider value, clamped to its range.</summary>
    public static void SetValue(TrackBar slider, int value) =>
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);

    /// <summary>Sets a numeric box value, clamped to its range.</summary>
    public static void SetValue(NumericUpDown box, double value) =>
        box.Value = Math.Clamp((decimal)value, box.Minimum, box.Maximum);

    /// <summary>Yes / No question; true for Yes.</summary>
    public static bool Confirm(IWin32Window owner, string text, string title = "Confirmation") =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
}

/// <summary>Input box for one line of text (scene name, preset name...).</summary>
internal static class InputDialog
{
    /// <returns>The typed text (not empty), or null when cancelled.</returns>
    public static string? Ask(IWin32Window? owner, string title, string prompt, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(440, 150),
            Font = new Font("Segoe UI", 9f),
        };
        var label = new Label { Text = prompt, Location = new Point(12, 12), Size = new Size(416, 50) };
        var box = new TextBox { Text = initial, Location = new Point(12, 66), Width = 416 };
        var ok = new Button { Text = Tr.T("OK"), DialogResult = DialogResult.OK, Location = new Point(266, 108), Width = 78 };
        var cancel = new Button { Text = Tr.T("Annuler"), DialogResult = DialogResult.Cancel, Location = new Point(350, 108), Width = 78 };
        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        var result = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
        if (result != DialogResult.OK)
        {
            return null;
        }
        string text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
