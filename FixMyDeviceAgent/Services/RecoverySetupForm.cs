using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class RecoverySetupForm : Form
{
    private readonly CheckedListBox _checkedListBox;
    private readonly IReadOnlyList<RecoveryApprovedLocation> _locations;

    public RecoverySetupForm(
        IReadOnlyList<RecoveryApprovedLocation> locations,
        Func<RecoveryApprovedLocation, string> resolveDisplayPath,
        IReadOnlyCollection<string>? preselectedPaths = null)
    {
        _locations = locations;

        Text = "Emergency Recovery";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(620, 430);

        var titleLabel = new Label
        {
            AutoSize = false,
            Text = "Choose recovery locations",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Location = new Point(18, 18),
            Size = new Size(400, 28),
        };

        var helpLabel = new Label
        {
            AutoSize = false,
            Text = "Select the folders and drives that Fix My Device is allowed to scan for recovery inventory. Leave everything unchecked to disable Emergency Recovery.",
            Location = new Point(18, 50),
            Size = new Size(584, 42),
        };

        _checkedListBox = new CheckedListBox
        {
            CheckOnClick = true,
            Location = new Point(18, 104),
            Size = new Size(584, 260),
        };

        foreach (var location in _locations)
        {
            var displayPath = resolveDisplayPath(location);
            var itemText = $"{location.Label}  -  {displayPath}";
            var isChecked = preselectedPaths?.Contains(location.FullPath, StringComparer.OrdinalIgnoreCase) ?? true;
            _checkedListBox.Items.Add(itemText, isChecked);
        }

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(444, 380),
            Size = new Size(76, 30),
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(526, 380),
            Size = new Size(76, 30),
        };

        Controls.Add(titleLabel);
        Controls.Add(helpLabel);
        Controls.Add(_checkedListBox);
        Controls.Add(saveButton);
        Controls.Add(cancelButton);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public RecoveryConfig BuildRecoveryConfig()
    {
        var selectedLocations = new List<RecoveryApprovedLocation>();

        for (var index = 0; index < _locations.Count; index++)
        {
            if (_checkedListBox.GetItemChecked(index))
            {
                selectedLocations.Add(_locations[index]);
            }
        }

        return new RecoveryConfig
        {
            Enabled = selectedLocations.Count > 0,
            ApprovedLocations = selectedLocations,
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        };
    }
}
