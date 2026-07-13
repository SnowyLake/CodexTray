using System.Globalization;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace CodexTray.App;

/// <summary>
/// A minimal numeric input with up/down spinner buttons and range clamping.
/// </summary>
internal sealed partial class NumericUpDown : UserControl
{
    /// <summary>
    /// The current value as text, kept as string to match the settings view model.
    /// </summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(string),
        typeof(NumericUpDown),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>
    /// The smallest value the spinner can reach.
    /// </summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(int),
        typeof(NumericUpDown),
        new PropertyMetadata(0));

    /// <summary>
    /// The largest value the spinner can reach.
    /// </summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(int),
        typeof(NumericUpDown),
        new PropertyMetadata(int.MaxValue));

    /// <summary>
    /// The fallback value used when the current text is empty or invalid.
    /// </summary>
    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue),
        typeof(int),
        typeof(NumericUpDown),
        new PropertyMetadata(0));

    /// <summary>
    /// Creates the numeric up/down control.
    /// </summary>
    public NumericUpDown()
    {
        InitializeComponent();
        PART_Up.Click += (_, _) => Step(1);
        PART_Down.Click += (_, _) => Step(-1);
    }

    /// <summary>
    /// Gets or sets the current value as text.
    /// </summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// Gets or sets the smallest reachable value.
    /// </summary>
    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>
    /// Gets or sets the largest reachable value.
    /// </summary>
    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>
    /// Gets or sets the fallback value for empty or invalid text.
    /// </summary>
    public int DefaultValue
    {
        get => (int)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    /// <summary>
    /// Applies a spinner step and clamps the result to the range.
    /// </summary>
    private void Step(int delta)
    {
        int current = int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : DefaultValue;
        int next = Math.Clamp(current + delta, Minimum, Maximum);
        Value = next.ToString(CultureInfo.InvariantCulture);
    }
}
