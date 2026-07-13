using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using DataObject = System.Windows.DataObject;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexTray.App;

/// <summary>
/// Attached behavior that restricts a TextBox to non-negative integer input.
/// </summary>
internal static partial class NumericInput
{
    /// <summary>
    /// Enables digit-only input filtering on a TextBox.
    /// </summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(NumericInput),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>
    /// Gets the digit-only input flag for a TextBox.
    /// </summary>
    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    /// <summary>
    /// Sets the digit-only input flag for a TextBox.
    /// </summary>
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    /// <summary>
    /// Attaches or detaches the input filter when the flag changes.
    /// </summary>
    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBox textBox)
        {
            return;
        }

        if (args.NewValue is true)
        {
            textBox.PreviewTextInput += OnPreviewTextInput;
            DataObject.AddPastingHandler(textBox, OnPaste);
        }
        else
        {
            textBox.PreviewTextInput -= OnPreviewTextInput;
            DataObject.RemovePastingHandler(textBox, OnPaste);
        }
    }

    /// <summary>
    /// Rejects typed input that would produce a non-digit value.
    /// </summary>
    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs args)
    {
        args.Handled = !IsDigits(args.Text);
    }

    /// <summary>
    /// Rejects pasted content that is not composed of digits.
    /// </summary>
    private static void OnPaste(object sender, DataObjectPastingEventArgs args)
    {
        if (args.DataObject.GetData(typeof(string)) is string text && IsDigits(text))
        {
            return;
        }

        args.CancelCommand();
    }

    /// <summary>
    /// Returns true when the text contains only decimal digits.
    /// </summary>
    private static bool IsDigits(string text)
    {
        return text.Length > 0 && DigitsRegex().IsMatch(text);
    }

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex DigitsRegex();
}
