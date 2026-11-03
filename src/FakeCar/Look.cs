using System.Drawing;
using System.Windows.Forms;

namespace FakeCar;

/// <summary>
/// The E-Sys MAX look: the launcher's white theme, its orange accent and Segoe UI. Every Fake Car window derives from
/// <see cref="Window"/>, which puts it on as the window loads; the clock icon's menu uses <see cref="Menu"/>.
/// </summary>
static class Look
{
    public static readonly Color Page = Color.FromArgb(0xF3, 0xF3, 0xF3);
    public static readonly Color Card = Color.FromArgb(0xFB, 0xFB, 0xFB);
    public static readonly Color Line = Color.FromArgb(0xD9, 0xD9, 0xD9);
    public static readonly Color Ink = Color.FromArgb(0x15, 0x15, 0x15);
    public static readonly Color Muted = Color.FromArgb(0x5C, 0x5C, 0x5C);
    public static readonly Color Accent = Color.FromArgb(0xE0, 0x7A, 0x2F);
    public static readonly Color AccentText = Color.FromArgb(0xB8, 0x5A, 0x14);
    public static readonly Color Picked = Color.FromArgb(0xFB, 0xEA, 0xDC);
    public static readonly Color Button = Color.FromArgb(0xEB, 0xEB, 0xEB);
    public static readonly Color ButtonHover = Color.FromArgb(0xE0, 0xE0, 0xE0);

    public static Icon Icon(int size)
    {
        using var stream = typeof(Look).Assembly.GetManifestResourceStream("FakeCar.fakecar.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, new Size(size, size));
    }

    public static Font Text => new("Segoe UI", 9.5f);
    public static Font Title => new("Segoe UI Semibold", 11f);

    /// <summary>Colours a window and everything in it, and whatever is added to it later.</summary>
    public static void Apply(Control control)
    {
        if (control is Form form)
        {
            form.BackColor = Page;
            form.ForeColor = Ink;
            if (form.AcceptButton is Button main) MainButton(main);
        }
        Style(control);
        control.ControlAdded += (_, e) => { if (e.Control is { } c) { Style(c); foreach (Control child in c.Controls) Style(child); } };
        foreach (Control child in control.Controls) Apply(child);
    }

    static void Style(Control c)
    {
        if (c.ForeColor == SystemColors.GrayText) c.ForeColor = Muted;
        switch (c)
        {
            case LinkLabel link:
                var muted = link.ForeColor == Muted;
                link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = muted ? Muted : AccentText;
                link.DisabledLinkColor = Muted;
                link.LinkBehavior = LinkBehavior.HoverUnderline;
                break;
            case Button button when button.BackColor != Accent:
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Button;
                button.ForeColor = Ink;
                button.FlatAppearance.BorderColor = Line;
                button.FlatAppearance.MouseOverBackColor = ButtonHover;
                button.FlatAppearance.MouseDownBackColor = Line;
                button.UseVisualStyleBackColor = false;
                break;
            case ListBox or TextBox or ComboBox:
                c.BackColor = Card;
                c.ForeColor = Ink;
                if (c is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
                // A plain list picks its row in the accent tint, not the system blue.
                if (c is ListBox { DrawMode: DrawMode.Normal } list)
                {
                    list.DrawMode = DrawMode.OwnerDrawFixed;
                    list.DrawItem += DrawRow;
                }
                break;
            case CheckBox box:
                box.ForeColor = Ink;
                break;
        }
    }

    static void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Picked : list.BackColor);
        e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            using var mark = new SolidBrush(Accent);
            e.Graphics.FillRectangle(mark, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
        }
        TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), list.Font, new Rectangle(e.Bounds.X + 2, e.Bounds.Y, e.Bounds.Width - 2, e.Bounds.Height), Ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>The window's main button, filled with the accent as in the launcher.</summary>
    public static void MainButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xD0, 0x6C, 0x22);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xB8, 0x5A, 0x14);
        button.UseVisualStyleBackColor = false;
    }

    /// <summary>The clock icon's menu in the same colours.</summary>
    public static void Menu(ToolStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new Colors()) { RoundedEdges = false };
        menu.BackColor = Card;
        menu.ForeColor = Ink;
        menu.Font = Text;
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Card;
        public override Color ImageMarginGradientBegin => Card;
        public override Color ImageMarginGradientMiddle => Card;
        public override Color ImageMarginGradientEnd => Card;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Picked;
        public override Color MenuItemSelected => Picked;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Card;
        public override Color CheckBackground => Picked;
        public override Color CheckSelectedBackground => Picked;
        public override Color CheckPressedBackground => Picked;
    }
}

/// <summary>A Fake Car window: the E-Sys MAX logo and look.</summary>
class Window : Form
{
    public Window()
    {
        Font = Look.Text;
        Icon = Look.Icon(32);
    }

    protected override void OnLoad(EventArgs e)
    {
        Look.Apply(this);
        base.OnLoad(e);
    }
}

/// <summary>A thin bar in the accent colour (the system progress bar keeps its own green).</summary>
sealed class Bar : Control
{
    int value;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum { get; set; } = 100;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => value;
        set { if (this.value != value) { this.value = value; Invalidate(); } }
    }

    public Bar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var track = new SolidBrush(Look.Line);
        using var fill = new SolidBrush(Look.Accent);
        e.Graphics.FillRectangle(track, ClientRectangle);
        var width = Maximum <= 0 ? 0 : (int)((long)ClientSize.Width * Math.Clamp(value, 0, Maximum) / Maximum);
        e.Graphics.FillRectangle(fill, 0, 0, width, ClientSize.Height);
    }
}
