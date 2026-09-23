namespace SnapshotAssistant;

public static class PlainTextClipboard
{
    public static void SetText(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, false, text);
        Clipboard.SetDataObject(data, true, 5, 100);
    }
}
