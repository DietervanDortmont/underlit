using System.Windows;

namespace Underlit.UI;

/// <summary>
/// First-run welcome dialog (v0.6.48). Surfaces the default hotkeys and
/// tells the user where to find the tray icon. Closes on the Got it
/// button; the host then sets <c>AppSettings.HasSeenIntro = true</c> so
/// the dialog never appears again for this user.
/// </summary>
public partial class IntroWindow : Window
{
    public IntroWindow()
    {
        InitializeComponent();
        BtnDismiss.Click += (_, _) => Close();
    }
}
