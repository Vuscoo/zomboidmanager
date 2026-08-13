namespace ZomboidManager;

public static class AppTheme
{
    public static readonly Color BackgroundDark = Color.FromArgb(24, 20, 32);
    public static readonly Color BackgroundPanel = Color.FromArgb(32, 27, 43);
    public static readonly Color BackgroundInput = Color.FromArgb(42, 36, 56);
    public static readonly Color AccentPurple = Color.FromArgb(138, 92, 246);
    public static readonly Color AccentPurpleHover = Color.FromArgb(160, 115, 255);
    public static readonly Color AccentPurpleDark = Color.FromArgb(105, 65, 200);
    public static readonly Color TextPrimary = Color.FromArgb(240, 238, 245);
    public static readonly Color TextSecondary = Color.FromArgb(160, 155, 175);
    public static readonly Color SuccessGreen = Color.FromArgb(70, 200, 130);
    public static readonly Color ErrorRed = Color.FromArgb(230, 80, 90);
    public static readonly Color WarningYellow = Color.FromArgb(240, 180, 60);
    public static readonly Color BorderColor = Color.FromArgb(55, 48, 68);

    private static readonly Font UiFont = new("Segoe UI", 9.5f);
    private static readonly Font UiFontBold = new("Segoe UI", 9.5f, FontStyle.Bold);

    public static void StyleButton(Button btn)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.BackColor = AccentPurple;
        btn.ForeColor = TextPrimary;
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = AccentPurpleHover;
        btn.FlatAppearance.MouseDownBackColor = AccentPurpleDark;
        btn.Font = UiFont;
        btn.Cursor = Cursors.Hand;

        btn.MouseEnter -= OnButtonMouseEnter;
        btn.MouseLeave -= OnButtonMouseLeave;
        btn.MouseEnter += OnButtonMouseEnter;
        btn.MouseLeave += OnButtonMouseLeave;
    }

    public static void StyleTextBox(TextBox tb)
    {
        tb.BackColor = BackgroundInput;
        tb.ForeColor = TextPrimary;
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.Font = UiFont;
    }

    public static void StyleGroupBox(GroupBox gb)
    {
        gb.ForeColor = TextPrimary;
        gb.Font = UiFontBold;
    }

    public static void StyleLabel(Label lbl, bool secondary = false)
    {
        lbl.ForeColor = secondary ? TextSecondary : TextPrimary;
        lbl.Font = UiFont;
    }

    private static void OnButtonMouseEnter(object? sender, EventArgs e)
    {
        if (sender is Button btn)
            btn.BackColor = AccentPurpleHover;
    }

    private static void OnButtonMouseLeave(object? sender, EventArgs e)
    {
        if (sender is Button btn)
            btn.BackColor = AccentPurple;
    }
}
