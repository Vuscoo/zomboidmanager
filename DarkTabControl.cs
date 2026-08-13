namespace ZomboidManager;

public class DarkTabControl : TabControl
{
    public DarkTabControl()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(140, 40);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = AppTheme.BackgroundDark;
        ForeColor = AppTheme.TextPrimary;
        Padding = new Point(12, 6);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabCount)
            return;

        TabPage page = TabPages[e.Index];
        Rectangle bounds = e.Bounds;
        bool selected = e.Index == SelectedIndex;

        Color backColor = selected ? AppTheme.AccentPurple : AppTheme.BackgroundPanel;
        Color textColor = selected ? AppTheme.TextPrimary : AppTheme.TextSecondary;
        FontStyle fontStyle = selected ? FontStyle.Bold : FontStyle.Regular;

        using (var backBrush = new SolidBrush(backColor))
            e.Graphics.FillRectangle(backBrush, bounds);

        using (var borderPen = new Pen(AppTheme.BorderColor))
            e.Graphics.DrawRectangle(borderPen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

        using var font = new Font("Segoe UI", 9.5f, fontStyle);
        TextRenderer.DrawText(
            e.Graphics,
            page.Text,
            font,
            bounds,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        using var brush = new SolidBrush(AppTheme.BackgroundDark);
        pevent.Graphics.FillRectangle(brush, ClientRectangle);
    }
}
