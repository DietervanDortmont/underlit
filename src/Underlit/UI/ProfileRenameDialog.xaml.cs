using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Underlit.UI;

/// <summary>
/// Lightweight modal for renaming a warmth profile. Shows a single text field,
/// OK/Cancel, and an inline warning when the proposed name collides with another
/// profile. Dialog returns true (with NewName populated) on OK.
/// </summary>
public partial class ProfileRenameDialog : Window
{
    /// <summary>Resulting new name. Only meaningful when ShowDialog returns true.</summary>
    public string NewName { get; private set; } = string.Empty;

    private readonly HashSet<string> _existingNames;

    public ProfileRenameDialog(string currentName, IEnumerable<string> otherProfileNames)
    {
        InitializeComponent();
        TxtName.Text = currentName;
        TxtName.SelectAll();
        TxtName.Focus();
        _existingNames = new HashSet<string>(otherProfileNames, System.StringComparer.OrdinalIgnoreCase);

        TxtName.TextChanged += (_, _) => Revalidate();
        BtnOk.Click += (_, _) =>
        {
            if (!Revalidate()) return;
            NewName = TxtName.Text.Trim();
            DialogResult = true;
        };
    }

    /// <summary>Updates the inline warning + OK button enabled state. Returns
    /// true if the current name is acceptable.</summary>
    private bool Revalidate()
    {
        string n = TxtName.Text.Trim();
        if (string.IsNullOrEmpty(n))
        {
            ShowWarning("Name can't be empty.");
            return false;
        }
        if (_existingNames.Contains(n))
        {
            ShowWarning("Another profile already uses that name.");
            return false;
        }
        LblWarning.Visibility = Visibility.Collapsed;
        BtnOk.IsEnabled = true;
        return true;
    }

    private void ShowWarning(string msg)
    {
        LblWarning.Text = msg;
        LblWarning.Visibility = Visibility.Visible;
        BtnOk.IsEnabled = false;
    }
}
