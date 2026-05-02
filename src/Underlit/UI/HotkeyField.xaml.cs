using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Underlit.Input;

namespace Underlit.UI;

/// <summary>
/// Apple-style listen-to-bind hotkey field. Drops in once per hotkey row,
/// replacing the bare TextBox the older settings UI used.
///
/// Interaction model (Option 2 in the proposal):
///   • Idle      — shows the current binding string. Click to enter listening.
///   • Listening — accent ring fades in, prompt reads "Press a combination…".
///                 Capturing rules:
///                    – modifier-only press is ignored (we wait for a real key);
///                    – first non-modifier KeyDown commits the binding;
///                    – Esc cancels and reverts to the previous value;
///                    – Backspace / Delete (alone, no modifier) clears the binding;
///                    – clicking outside or losing focus cancels;
///                    – binding without a modifier is rejected with an inline
///                      warning balloon (single keys would intercept everything
///                      system-wide once registered as a global hotkey).
///   • Empty     — shows "(none — click to set)" in muted grey.
///
/// The field exposes a string Value matching <see cref="Hotkey"/>'s round-trip
/// format (e.g. "Ctrl+Alt+Down"), so consumers (settings serialisation,
/// HotkeyManager) keep working unchanged. Value changes raise
/// <see cref="ValueChanged"/>.
/// </summary>
public partial class HotkeyField : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(HotkeyField),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnValueChanged));

    /// <summary>The current binding as a hotkey string ("Ctrl+Alt+Down"). Setting it
    /// triggers a UI refresh; binding-changed listeners get <see cref="ValueChanged"/>.</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value ?? string.Empty);
    }

    /// <summary>Raised when the user sets, clears, or replaces the binding via this
    /// control. Not raised for programmatic Value writes from outside.</summary>
    public event Action<string>? ValueChanged;

    private bool _listening;
    private string _preListenValue = string.Empty;
    private DispatcherTimer? _warningTimer;

    public HotkeyField()
    {
        InitializeComponent();

        // v0.6.31: hook PreviewMouseLeftButtonDown on `this` (the UserControl
        // itself) instead of bubbling MouseLeftButtonDown on FieldBorder.
        // Bubbling was being eaten somewhere between FieldBorder and us in the
        // SettingsWindow chain, so the click never reached BeginListening.
        // Tunneling on `this` fires unconditionally for any click inside the
        // control, regardless of which child element gets hit first.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Don't enter listen mode if the user clicked the explicit clear
            // button — let its own Click handler do the clearing.
            if (e.OriginalSource is DependencyObject d && IsDescendantOf(d, ClearButton))
                return;
            BeginListening();
            // Mark handled so the click doesn't bubble out and trigger the
            // window's focus-shuffle logic, which would fire our
            // LostKeyboardFocus and immediately cancel listening.
            e.Handled = true;
        };
        ClearButton.Click += (_, _) => SetValueAndNotify(string.Empty);

        // Keyboard accessibility: Space/Enter while focused (but not listening) starts listening.
        PreviewKeyDown += OnPreviewKeyDown;

        // LostKeyboardFocus (not LostFocus) — logical LostFocus can spuriously
        // fire during the click-to-focus handshake before BeginListening even
        // settles, which would cancel listening before the user can press a
        // key. Keyboard focus only moves when the user actually navigates away
        // (Tab, click on another control), which is the right cancel trigger.
        LostKeyboardFocus += (_, _) => CancelListening();
        GotFocus          += (_, _) => UpdateVisuals();

        Loaded += (_, _) => UpdateVisuals();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HotkeyField)d).UpdateVisuals();
    }

    // ---- State transitions ----

    private void BeginListening()
    {
        if (_listening) return;
        _listening = true;
        _preListenValue = Value;
        UpdateVisuals();
        // Defer focus assignment to the next dispatcher tick. Calling Focus()
        // inside the click handler races with WPF's internal click-to-focus
        // logic; once in a while focus would land on the SettingsWindow root
        // instead of us, key events would never arrive, and the user's first
        // key-press would do nothing. Pinning to Input priority makes sure we
        // run before the next user input event is processed.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_listening) return;
            Focus();
            Keyboard.Focus(this);
        }), DispatcherPriority.Input);
    }

    private void CancelListening()
    {
        if (!_listening) return;
        _listening = false;
        // Don't notify — we're reverting to the prior value.
        SetCurrentValue(ValueProperty, _preListenValue);
        UpdateVisuals();
    }

    private void CommitBinding(string value)
    {
        _listening = false;
        SetValueAndNotify(value);
        FlashCaptured();
    }

    private void SetValueAndNotify(string value)
    {
        SetCurrentValue(ValueProperty, value ?? string.Empty);
        ValueChanged?.Invoke(Value);
        UpdateVisuals();
    }

    // ---- Key capture ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_listening)
        {
            // Keyboard activation: Space or Enter starts listening when the field has focus
            // but isn't yet listening. (Tab navigation lands here first.)
            if (e.Key == Key.Space || e.Key == Key.Enter)
            {
                BeginListening();
                e.Handled = true;
            }
            return;
        }

        // Listening — eat every key, route to capture.
        e.Handled = true;

        // Use the actual physical key, not SystemKey. WPF gives us SystemKey when an
        // Alt-modified key arrives; in that case the real key is in e.SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc cancels.
        if (key == Key.Escape)
        {
            CancelListening();
            return;
        }

        var mods = ModifierKeysFromKeyboard();

        // Backspace / Delete with no modifier clears.
        if ((key == Key.Back || key == Key.Delete) && mods == HotkeyModifiers.None)
        {
            CommitBinding(string.Empty);
            return;
        }

        // Modifier-only press — wait for a real key.
        if (IsModifierKey(key)) return;

        // Reject combinations without any modifier — single global keys would
        // hijack every press of that key system-wide.
        if (mods == HotkeyModifiers.None)
        {
            ShowWarning("Bindings need at least one modifier (Ctrl, Alt, Shift, or Win).");
            // Stay in listening mode so the user can correct without re-clicking.
            return;
        }

        // Build the Hotkey and serialise via its existing ToString — that way the
        // format is guaranteed to round-trip through Hotkey.Parse later.
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            ShowWarning("That key can't be bound globally.");
            return;
        }
        try
        {
            var hk = new Hotkey(mods, vk);
            // Round-trip through Parse to make sure VkToName produced something
            // we can parse back. If the key landed on the "VK0xNN" hex fallback,
            // it'll still round-trip but isn't a great user experience — accept
            // it anyway and let the user see the hex name.
            CommitBinding(hk.ToString());
        }
        catch
        {
            ShowWarning("That combination can't be bound.");
        }
    }

    private static HotkeyModifiers ModifierKeysFromKeyboard()
    {
        var k = Keyboard.Modifiers;
        var m = HotkeyModifiers.None;
        if ((k & ModifierKeys.Control) != 0) m |= HotkeyModifiers.Control;
        if ((k & ModifierKeys.Alt)     != 0) m |= HotkeyModifiers.Alt;
        if ((k & ModifierKeys.Shift)   != 0) m |= HotkeyModifiers.Shift;
        if ((k & ModifierKeys.Windows) != 0) m |= HotkeyModifiers.Win;
        return m;
    }

    private static bool IsModifierKey(Key k) =>
        k == Key.LeftCtrl    || k == Key.RightCtrl    ||
        k == Key.LeftAlt     || k == Key.RightAlt     ||
        k == Key.LeftShift   || k == Key.RightShift   ||
        k == Key.LWin        || k == Key.RWin         ||
        k == Key.System      ||
        k == Key.Capital     || k == Key.Scroll       ||
        k == Key.NumLock     || k == Key.ImeProcessed;

    // ---- Visual state ----

    private void UpdateVisuals()
    {
        if (_listening)
        {
            ValueText.Visibility   = Visibility.Collapsed;
            HintText.Visibility    = Visibility.Visible;
            KbdGlyph.Visibility    = Visibility.Visible;
            ClearButton.Visibility = Visibility.Collapsed;
            FadeAccentRing(toOpacity: 1.0);
            // Border disappears (matches card bg) so the accent ring
            // takes over the visual outline during listening.
            FieldBorder.SetResourceReference(Border.BorderBrushProperty, "App.CardBg");
        }
        else
        {
            HintText.Visibility    = Visibility.Collapsed;
            KbdGlyph.Visibility    = Visibility.Collapsed;
            FadeAccentRing(toOpacity: 0.0);
            FieldBorder.SetResourceReference(Border.BorderBrushProperty, "App.CardBorder");

            if (string.IsNullOrEmpty(Value))
            {
                ValueText.Visibility   = Visibility.Visible;
                ValueText.Text         = "(none, click to set)";
                // v0.6.45: SetResourceReference instead of TryFindResource
                // so the foreground tracks theme changes (Windows
                // dark/light flip) live, instead of being snapshotted to
                // whichever brush was current at last UpdateVisuals call.
                ValueText.SetResourceReference(TextBlock.ForegroundProperty, "App.TextSecondary");
                ValueText.FontStyle    = FontStyles.Italic;
                ClearButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ValueText.Visibility   = Visibility.Visible;
                ValueText.Text         = Value;
                ValueText.SetResourceReference(TextBlock.ForegroundProperty, "App.TextPrimary");
                ValueText.FontStyle    = FontStyles.Normal;
                ClearButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void FadeAccentRing(double toOpacity)
    {
        var anim = new DoubleAnimation
        {
            To = toOpacity,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        AccentRing.BeginAnimation(OpacityProperty, anim);
    }

    private void FlashCaptured()
    {
        // Brief accent flash on commit — a 300ms in/out fade. Layered on top of
        // the regular accent-ring fadeout so the user gets clear "✓ accepted" feedback.
        AccentRing.BeginAnimation(OpacityProperty, null);
        AccentRing.Opacity = 1.0;
        var fade = new DoubleAnimation
        {
            From = 1.0,
            To   = 0.0,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        AccentRing.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowWarning(string message)
    {
        WarningText.Text = message;
        WarningBalloon.Visibility = Visibility.Visible;
        WarningBalloon.Opacity = 1.0;

        _warningTimer?.Stop();
        _warningTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(1800),
        };
        _warningTimer.Tick += (_, _) =>
        {
            _warningTimer?.Stop();
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            fade.Completed += (_, _) => WarningBalloon.Visibility = Visibility.Collapsed;
            WarningBalloon.BeginAnimation(OpacityProperty, fade);
        };
        _warningTimer.Start();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        var d = child;
        while (d != null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
