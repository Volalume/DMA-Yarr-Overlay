using System.Drawing;
using System.Windows.Forms;

namespace Overlay;

internal sealed class BrandingDialog : Form
{
    private readonly TextBox _name;
    private readonly TextBox _title;
    private readonly TextBox _windowClass;
    private readonly TextBox _icon;

    public string AppDisplayName => _name.Text;
    public string WindowTitle => _title.Text;
    public string WindowClassName => _windowClass.Text;
    public string IconPath => _icon.Text;

    public BrandingDialog(string appDisplayName, string windowTitle, string windowClassName, string iconPath)
    {
        Text = "Application Branding";
        ClientSize = new Size(520, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var processName = Path.GetFileName(Environment.ProcessPath) ?? "Overlay.exe";
        var note = new Label
        {
            AutoSize = false,
            Location = new Point(20, 16),
            Size = new Size(480, 38),
            Text = $"Process: {processName} (rename the EXE before launch to change it)\r\nWindow class base changes are applied after restart."
        };
        Controls.Add(note);

        _name = AddField("Display name", appDisplayName, 62);
        _title = AddField("Window title", windowTitle, 104);
        _windowClass = AddField("Class base", windowClassName, 146);
        _icon = AddField("ICO file", iconPath, 188);

        var browse = new Button { Text = "Browse...", Location = new Point(408, 185), Size = new Size(90, 28) };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Windows icon (*.ico)|*.ico", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) == DialogResult.OK) _icon.Text = picker.FileName;
        };
        Controls.Add(browse);

        var clear = new Button { Text = "Default Icon", Location = new Point(306, 226), Size = new Size(100, 30) };
        clear.Click += (_, _) => _icon.Text = "";
        Controls.Add(clear);
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(408, 226), Size = new Size(90, 30) };
        Controls.Add(save);
        AcceptButton = save;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(408, 262), Size = new Size(90, 30) };
        Controls.Add(cancel);
        CancelButton = cancel;
    }

    private TextBox AddField(string label, string value, int top)
    {
        Controls.Add(new Label { Text = label, Location = new Point(20, top + 4), Size = new Size(100, 24) });
        var box = new TextBox { Text = value, Location = new Point(124, top), Size = new Size(274, 28) };
        Controls.Add(box);
        return box;
    }
}
