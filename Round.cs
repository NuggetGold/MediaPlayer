using System.Windows;

namespace MediaPlayer;

public static class Round
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius",
        typeof(CornerRadius),
        typeof(Round),
        new FrameworkPropertyMetadata(new CornerRadius(0)));

    public static CornerRadius GetRadius(DependencyObject element)
    {
        return (CornerRadius)element.GetValue(RadiusProperty);
    }

    public static void SetRadius(DependencyObject element, CornerRadius value)
    {
        element.SetValue(RadiusProperty, value);
    }
}
