using Microsoft.Maui.Controls.Shapes;

namespace ArriLight.Controls;

public sealed class StatusIndicator : ContentView
{
    private const string BlinkAnimationName = "StatusBlink";
    private readonly Ellipse _dot;

    public static readonly BindableProperty FillProperty = BindableProperty.Create(
        nameof(Fill), typeof(Brush), typeof(StatusIndicator), null,
        propertyChanged: (bindable, _, value) => ((StatusIndicator)bindable)._dot.Fill = (Brush?)value ?? Brush.Transparent);

    public static readonly BindableProperty IsBlinkingProperty = BindableProperty.Create(
        nameof(IsBlinking), typeof(bool), typeof(StatusIndicator), false,
        propertyChanged: OnAnimationPropertyChanged);

    public static readonly BindableProperty IsAnimationEnabledProperty = BindableProperty.Create(
        nameof(IsAnimationEnabled), typeof(bool), typeof(StatusIndicator), false,
        propertyChanged: OnAnimationPropertyChanged);

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool IsBlinking
    {
        get => (bool)GetValue(IsBlinkingProperty);
        set => SetValue(IsBlinkingProperty, value);
    }

    public bool IsAnimationEnabled
    {
        get => (bool)GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public StatusIndicator()
    {
        _dot = new Ellipse();
        Content = _dot;
        Loaded += (_, _) => UpdateBlinking();
        Unloaded += (_, _) => StopBlinking();
    }

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        StopBlinking();
        base.OnHandlerChanging(args);
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        UpdateBlinking();
    }

    private bool ShouldAnimate => IsBlinking && IsAnimationEnabled && IsLoaded && Handler is not null;

    private static void OnAnimationPropertyChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((StatusIndicator)bindable).UpdateBlinking();

    private void UpdateBlinking()
    {
        if (!ShouldAnimate)
        {
            StopBlinking();
            return;
        }
        if (this.AnimationIsRunning(BlinkAnimationName))
            return;

        var animation = new Animation();
        animation.Add(0, 0.5, new Animation(value => Opacity = value, 1, 0.15));
        animation.Add(0.5, 1, new Animation(value => Opacity = value, 0.15, 1));
        animation.Commit(this, BlinkAnimationName, length: 900, repeat: () => ShouldAnimate);
    }

    private void StopBlinking()
    {
        this.AbortAnimation(BlinkAnimationName);
        Opacity = 1;
    }
}
