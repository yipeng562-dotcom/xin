namespace SnapshotAssistant;

public sealed class DarkTabControl : TabControl
{
    public Color HeaderBackColor { get; set; } = Color.FromArgb(36, 41, 47);
    public Color SelectedTabColor { get; set; } = Color.FromArgb(29, 119, 177);
    public Color TabColor { get; set; } = Color.FromArgb(47, 54, 63);
    public Color TextColor { get; set; } = Color.FromArgb(225, 231, 237);

    public DarkTabControl()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using(var header = new SolidBrush(HeaderBackColor))
            e.Graphics.FillRectangle(header, ClientRectangle);

        for(var index = 0; index < TabCount; index++)
        {
            var bounds = GetTabRect(index);
            var selected = index == SelectedIndex;
            using var background = new SolidBrush(selected ? SelectedTabColor : TabColor);
            using var foreground = new SolidBrush(TextColor);
            e.Graphics.FillRectangle(background, bounds);
            e.Graphics.DrawRectangle(Pens.DimGray, bounds);
            var text = TabPages[index].Text;
            var size = e.Graphics.MeasureString(text, Font);
            e.Graphics.DrawString(text, Font, foreground,
                bounds.Left + (bounds.Width - size.Width) / 2,
                bounds.Top + (bounds.Height - size.Height) / 2);
        }
    }
}
