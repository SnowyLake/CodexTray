using System.Windows;
using System.Windows.Data;
using Controls = System.Windows.Controls;

namespace CodexTray.App;

/// <summary>
/// Edits a credential through a native masked field with an explicit temporary reveal.
/// </summary>
internal partial class CredentialInput : Controls.UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(CredentialInput),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Initializes credential editors and clears reveal state when editing ends or the card changes.
    /// </summary>
    public CredentialInput()
    {
        InitializeComponent();
        RevealedInput.SetBinding(Controls.TextBox.TextProperty, new System.Windows.Data.Binding(nameof(Text))
        {
            Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        IsVisibleChanged += (_, args) =>
        {
            if (!(bool)args.NewValue)
            {
                SetRevealed(false);
            }
        };
        Unloaded += (_, _) => SetRevealed(false);
        DataContextChanged += (_, _) => SetRevealed(false);
    }

    /// <summary>
    /// Synchronizes external credential edits with the native masked input.
    /// </summary>
    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        CredentialInput input = (CredentialInput)sender;
        string text = args.NewValue as string ?? string.Empty;
        if (input.MaskedInput != null && input.MaskedInput.Password != text)
        {
            input.MaskedInput.Password = text;
        }
    }

    /// <summary>
    /// Updates the binding without replacing it when the user edits the masked credential.
    /// </summary>
    private void PasswordChanged(object sender, RoutedEventArgs args)
    {
        SetCurrentValue(TextProperty, MaskedInput.Password);
    }

    /// <summary>
    /// Toggles the visible editor without copying credentials to the clipboard.
    /// </summary>
    private void ToggleReveal(object sender, RoutedEventArgs args)
    {
        SetRevealed(RevealedInput.Visibility != Visibility.Visible);
    }

    /// <summary>
    /// Selects the visible credential editor and updates its accessible action text.
    /// </summary>
    private void SetRevealed(bool revealed)
    {
        MaskedInput.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        RevealedInput.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        HiddenEyeSlash.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        string action = revealed ? "Hide credential" : "Show credential";
        RevealButton.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(RevealButton, action);
    }
}
