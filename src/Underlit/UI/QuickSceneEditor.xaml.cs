using System;
using System.Windows;
using System.Windows.Controls;
using Underlit.Settings;

namespace Underlit.UI;

/// <summary>
/// Modal editor for one custom Quick Scene. Caller passes a scene to
/// edit (or null to create); on Save, <see cref="Result"/> holds the
/// saved scene and DialogResult is true.
///
/// v0.6.55. Self-contained dark palette so it doesn't depend on the
/// settings window's runtime theme brushes.
/// </summary>
public partial class QuickSceneEditor : Window
{
    /// <summary>Set on successful Save; null otherwise.</summary>
    public QuickScene? Result { get; private set; }

    private readonly QuickScene _editing;
    private readonly bool _isNew;

    public QuickSceneEditor(QuickScene? scene)
    {
        InitializeComponent();
        _isNew = scene == null;
        _editing = scene == null
            ? new QuickScene { Name = "My scene", Kelvin = 4500, BrightnessPercent = 100 }
            : new QuickScene
              {
                  Id                = scene.Id,
                  Name              = scene.Name,
                  Action            = scene.Action,
                  Kelvin            = scene.Kelvin,
                  BrightnessPercent = scene.BrightnessPercent,
              };

        LblTitle.Text   = _isNew ? "New Quick Scene" : "Edit Quick Scene";
        TxtName.Text    = _editing.Name;
        CboAction.SelectedIndex = (int)_editing.Action;
        SldKelvin.Value = _editing.Kelvin;
        SldBri.Value    = _editing.BrightnessPercent;
        UpdateLabels();
        UpdateRowEnable();

        SldKelvin.ValueChanged += (_, _) => UpdateLabels();
        SldBri.ValueChanged    += (_, _) => UpdateLabels();
        CboAction.SelectionChanged += (_, _) => UpdateRowEnable();

        BtnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        BtnSave.Click   += (_, _) => Save();
    }

    private void UpdateLabels()
    {
        LblKelvin.Text = $"{(int)SldKelvin.Value} K";
        LblBri.Text    = $"{(int)SldBri.Value} %";
    }

    private void UpdateRowEnable()
    {
        // Kelvin/brightness rows only matter for the Set action. Grey
        // them out for Off / Resume so the user knows those values
        // aren't being used.
        bool ctRelevant = CboAction.SelectedIndex == (int)QuickSceneAction.SetCtBri;
        WarmthRow.Opacity     = ctRelevant ? 1.0 : 0.45;
        BrightnessRow.Opacity = ctRelevant ? 1.0 : 0.45;
        WarmthRow.IsEnabled     = ctRelevant;
        BrightnessRow.IsEnabled = ctRelevant;
    }

    private void Save()
    {
        string name = (TxtName.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "Give your scene a name.", "Quick Scene",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtName.Focus();
            return;
        }

        Result = new QuickScene
        {
            Id                = _editing.Id,
            Name              = name,
            Action            = (QuickSceneAction)Math.Max(0, CboAction.SelectedIndex),
            Kelvin            = (int)SldKelvin.Value,
            BrightnessPercent = (int)SldBri.Value,
        };
        DialogResult = true;
        Close();
    }
}
