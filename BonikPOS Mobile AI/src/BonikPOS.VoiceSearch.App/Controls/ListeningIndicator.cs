using Microsoft.Maui.Controls.Shapes;

namespace BonikPOS.VoiceSearch.App.Controls;

/// <summary>Ring behind the microphone button that grows with the input level while listening.</summary>
public sealed class ListeningIndicator : Grid
{
    public static readonly BindableProperty LevelProperty =
        BindableProperty.Create(nameof(Level), typeof(double), typeof(ListeningIndicator), 0.0, propertyChanged: (b, _, _) => ((ListeningIndicator)b).Update());

    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(ListeningIndicator), false, propertyChanged: (b, _, _) => ((ListeningIndicator)b).Update());

    public static readonly BindableProperty RingColorProperty =
        BindableProperty.Create(nameof(RingColor), typeof(Color), typeof(ListeningIndicator), Colors.Teal, propertyChanged: (b, _, n) => ((ListeningIndicator)b)._ring.Fill = new SolidColorBrush((Color)n));

    private readonly Ellipse _ring = new() { WidthRequest = 132, HeightRequest = 132, Opacity = 0 };

    public ListeningIndicator()
    {
        Children.Add(_ring);
        InputTransparent = true;
        SemanticProperties.SetDescription(this, "Listening level indicator");
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Color RingColor
    {
        get => (Color)GetValue(RingColorProperty);
        set => SetValue(RingColorProperty, value);
    }

    private void Update()
    {
        double target = IsActive ? 1.05 + Math.Clamp(Level, 0, 1) * 0.55 : 1.0;
        _ring.Opacity = IsActive ? 0.25 + Math.Clamp(Level, 0, 1) * 0.3 : 0;
        _ring.AbortAnimation("level");
        _ = _ring.ScaleToAsync(target, 80, Easing.CubicOut);
    }
}
