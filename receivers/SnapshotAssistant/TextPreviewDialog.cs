namespace SnapshotAssistant;

public sealed class TextPreviewDialog : Form
{
    public TextPreviewDialog(string title, string text)
    {
        Text = title;
        Width = 900;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;

        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 10F),
            Text = text
        };
        var copy = new Button { Text = "复制", AutoSize = true };
        copy.Click += (_, _) => PlainTextClipboard.SetText(box.Text);
        var close = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6)
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);
        Controls.Add(box);
        Controls.Add(buttons);
        AcceptButton = close;
    }
}
