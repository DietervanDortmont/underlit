using System.Windows;
using System.Windows.Controls;

namespace Underlit.UI;

/// <summary>
/// v0.6.73: minimal single-line text-input dialog. Used by the lights
/// timeline profile rename action. Set <see cref="Prompt"/> and
/// <see cref="Value"/> before <c>ShowDialog</c>; <see cref="Value"/>
/// holds the user's text afterwards if they clicked OK / pressed Enter.
///
/// <para>Self-contained (no XAML) so we don't have to wire a whole new
/// .xaml + .cs pair for what's essentially a one-line MessageBox-style
/// prompt. WPF lacks a built-in text-prompt dialog so every project
/// ends up rolling its own.</para>
/// </summary>
public sealed class InputBox : Window
{
    private readonly TextBox _txt;

    public string Prompt
    {
        get => _promptLabel.Text;
        set => _promptLabel.Text = value;
    }

    public string Value
    {
        get => _txt.Text;
        set => _txt.Text = value;
    }

    private readonly TextBlock _promptLabel;

    public InputBox()
    {
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        _promptLabel = new TextBlock
        {
            Margin = new Thickness(16, 14, 16, 6),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        _txt = new TextBox
        {
            Margin = new Thickness(16, 0, 16, 14),
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
        };
        _txt.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                DialogResult = true;
                Close();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                DialogResult = false;
                Close();
                e.Handled = true;
            }
        };

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 80,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Width = 80,
            Height = 28,
        };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 14),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(_promptLabel);
        root.Children.Add(_txt);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _txt.Focus();
            _txt.SelectAll();
        };
    }
}
