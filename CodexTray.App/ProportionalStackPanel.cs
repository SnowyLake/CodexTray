using System.Windows;
using Controls = System.Windows.Controls;
using Size = System.Windows.Size;

namespace CodexTray.App;

internal sealed class ProportionalStackPanel : Controls.Panel
{
    private const double k_MinSegmentWidth = 6;

    public static readonly DependencyProperty ShareProperty = DependencyProperty.RegisterAttached(
        "Share",
        typeof(double),
        typeof(ProportionalStackPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap),
        typeof(double),
        typeof(ProportionalStackPanel),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>
    /// Gets the pixel gap drawn between proportional children.
    /// </summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>
    /// Gets the 0-100 share used to size a child inside the panel.
    /// </summary>
    public static double GetShare(DependencyObject element)
    {
        return (double)element.GetValue(ShareProperty);
    }

    /// <summary>
    /// Sets the 0-100 share used to size a child inside the panel.
    /// </summary>
    public static void SetShare(DependencyObject element, double value)
    {
        element.SetValue(ShareProperty, value);
    }

    /// <summary>
    /// Measures children against the available height and the panel width.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        if (double.IsInfinity(availableSize.Height))
        {
            return new Size(width, height);
        }

        return new Size(width, availableSize.Height);
    }

    /// <summary>
    /// Arranges children from left to right using their 0-100 shares of the panel width.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double gap = Math.Max(0, Gap);
        int lastIndex = InternalChildren.Count - 1;
        for (int index = 0; index < InternalChildren.Count; index++)
        {
            UIElement child = InternalChildren[index];
            double share = GetShare(child);
            double width = share > 0 && double.IsFinite(share)
                ? Math.Max(finalSize.Width * Math.Clamp(share, 0, 100) / 100d, k_MinSegmentWidth)
                : 0;
            if (x + width > finalSize.Width)
            {
                width = Math.Max(0, finalSize.Width - x);
            }

            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
            if (index < lastIndex && width > 0)
            {
                x += gap;
            }
        }

        return finalSize;
    }
}
