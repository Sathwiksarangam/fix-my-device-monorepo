namespace FixMyDeviceAgent.Services;

public sealed class SetupCodePromptForm : Form
{
    private readonly TextBox _setupCodeTextBox;

    public SetupCodePromptForm(string? initialSetupCode = null)
    {
        Text = "Fix My Device Agent";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(470, 180);

        var titleLabel = new Label
        {
            AutoSize = false,
            Text = "Enter your Agent Setup Code",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Location = new Point(18, 18),
            Size = new Size(420, 28),
        };

        var helpLabel = new Label
        {
            AutoSize = false,
            Text = "The code is shown in your Fix My Device dashboard and is only required when connecting or reconnecting this PC.",
            Location = new Point(18, 50),
            Size = new Size(430, 40),
        };

        _setupCodeTextBox = new TextBox
        {
            Location = new Point(18, 100),
            Size = new Size(430, 28),
            Text = initialSetupCode ?? string.Empty,
            CharacterCasing = CharacterCasing.Upper,
        };

        var connectButton = new Button
        {
            Text = "Connect Agent",
            DialogResult = DialogResult.OK,
            Location = new Point(247, 138),
            Size = new Size(118, 30),
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(372, 138),
            Size = new Size(76, 30),
        };

        Controls.Add(titleLabel);
        Controls.Add(helpLabel);
        Controls.Add(_setupCodeTextBox);
        Controls.Add(connectButton);
        Controls.Add(cancelButton);

        AcceptButton = connectButton;
        CancelButton = cancelButton;
    }

    public string SetupCode => _setupCodeTextBox.Text.Trim().ToUpperInvariant();
}
