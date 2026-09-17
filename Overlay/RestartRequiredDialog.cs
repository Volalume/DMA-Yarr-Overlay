using System.Drawing;
using System.Windows.Forms;

namespace Overlay;

internal sealed class RestartRequiredDialog : Form
{
    public RestartRequiredDialog(string windowClassName)
    {
        Text = "Restart Required";
        ClientSize = new Size(430, 145);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Controls.Add(new Label
        {
            Location = new Point(20, 18),
            Size = new Size(390, 54),
            Text = $"Window class '{windowClassName}' was saved.\r\nRestart the application to apply it to every window."
        });
        var restart = new Button { Text = "Restart", DialogResult = DialogResult.OK, Location = new Point(224, 92), Size = new Size(90, 34) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(320, 92), Size = new Size(90, 34) };
        Controls.Add(restart);
        Controls.Add(cancel);
        AcceptButton = restart;
        CancelButton = cancel;
    }
}
