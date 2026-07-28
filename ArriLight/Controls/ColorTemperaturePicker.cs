using Microsoft.Maui.Graphics;

namespace ArriLight.Controls;

public sealed class ColorTemperaturePicker : GraphicsView
{
    private const double DragThreshold = 8;

    private readonly ColorTemperatureDrawable _drawable;
    private PickerRegion _activeRegion;
    private Point _interactionStart;
    private Point _lastInteractionPoint;
    private bool _hasDragged;

    public event EventHandler? SelectionChanged;

    public double Hue
    {
        get => _drawable.Hue;
        set
        {
            _drawable.Hue = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    public double Saturation
    {
        get => _drawable.Saturation;
        set
        {
            _drawable.Saturation = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    public double Temperature
    {
        get => _drawable.Temperature;
        set
        {
            _drawable.Temperature = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    public Color SelectedColor => Color.FromHsla(Hue, Saturation, 0.52);

    public ColorTemperaturePicker()
    {
        _drawable = new ColorTemperatureDrawable();
        Drawable = _drawable;
        StartInteraction += OnStartInteraction;
        DragInteraction += OnDragInteraction;
        EndInteraction += OnEndInteraction;
    }

    public void SelectAt(Point position)
    {
        _activeRegion = ResolveRegion(position.X, position.Y);
        UpdateSelection(position.X, position.Y);
        _activeRegion = PickerRegion.None;
    }

    private void OnStartInteraction(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
            return;

        var point = e.Touches[0];
        _activeRegion = ResolveRegion(point.X, point.Y);
        _interactionStart = point;
        _lastInteractionPoint = point;
        _hasDragged = false;
    }

    private void OnDragInteraction(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0 || _activeRegion == PickerRegion.None)
            return;

        var point = e.Touches[0];
        _lastInteractionPoint = point;

        if (!_hasDragged)
        {
            var deltaX = point.X - _interactionStart.X;
            var deltaY = point.Y - _interactionStart.Y;
            _hasDragged =
                Math.Sqrt(deltaX * deltaX + deltaY * deltaY) >= DragThreshold;
        }

        if (!_hasDragged)
            return;

        UpdateSelection(point.X, point.Y);
    }

    private void OnEndInteraction(object? sender, TouchEventArgs e)
    {
        if (_activeRegion != PickerRegion.None && !_hasDragged)
            UpdateSelection(_lastInteractionPoint.X, _lastInteractionPoint.Y);

        _activeRegion = PickerRegion.None;
        _hasDragged = false;
    }

    private PickerRegion ResolveRegion(double x, double y)
    {
        var centerX = Width / 2;
        var centerY = Height / 2;
        var dx = x - centerX;
        var dy = y - centerY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var outerRadius = Math.Min(Width, Height) / 2 - 8;
        var colorRadius = outerRadius * 0.62;

        if (distance <= colorRadius)
            return PickerRegion.Color;

        return distance >= outerRadius * 0.68 && distance <= outerRadius * 1.15
            ? PickerRegion.Temperature
            : PickerRegion.None;
    }

    private void UpdateSelection(double x, double y)
    {
        var centerX = Width / 2;
        var centerY = Height / 2;
        var dx = x - centerX;
        var dy = y - centerY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var outerRadius = Math.Min(Width, Height) / 2 - 8;
        var colorRadius = outerRadius * 0.62;

        if (_activeRegion == PickerRegion.Color)
        {
            var angle = Math.Atan2(dy, dx);
            Hue = (angle / (Math.PI * 2) + 1) % 1;
            Saturation = Math.Clamp(distance / colorRadius, 0, 1);
        }
        else if (_activeRegion == PickerRegion.Temperature)
        {
            var degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (degrees < 0)
                degrees += 360;
            if (degrees < 135)
                degrees += 360;

            Temperature = Math.Clamp((degrees - 135) / 270, 0, 1);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private enum PickerRegion
    {
        None,
        Color,
        Temperature
    }
}

internal sealed class ColorTemperatureDrawable : IDrawable
{
    public double Hue { get; set; } = 0.08;
    public double Saturation { get; set; } = 0.78;
    public double Temperature { get; set; } = 0.32;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var center = dirtyRect.Center;
        var outerRadius = Math.Min(dirtyRect.Width, dirtyRect.Height) / 2f - 8f;
        var ringInnerRadius = outerRadius * 0.75f;
        var colorRadius = outerRadius * 0.62f;

        DrawTemperatureRing(canvas, center, ringInnerRadius, outerRadius);
        DrawColorDisc(canvas, center, colorRadius);
        DrawSelectionHandles(canvas, center, ringInnerRadius, outerRadius, colorRadius);
    }

    private void DrawTemperatureRing(
        ICanvas canvas,
        PointF center,
        float innerRadius,
        float outerRadius)
    {
        const int segmentCount = 108;
        for (var index = 0; index <= segmentCount; index++)
        {
            var progress = index / (double)segmentCount;
            var angle = DegreesToRadians(135 + progress * 270);
            var start = PointOnCircle(center, innerRadius, angle);
            var end = PointOnCircle(center, outerRadius, angle);

            canvas.StrokeColor = Interpolate(
                Color.FromArgb("#FFB36B"),
                Color.FromArgb("#D9ECFF"),
                progress);
            canvas.StrokeSize = 4;
            canvas.DrawLine(start, end);
        }
    }

    private static void DrawColorDisc(ICanvas canvas, PointF center, float radius)
    {
        const int segmentCount = 120;
        for (var index = 0; index < segmentCount; index++)
        {
            var startAngle = Math.PI * 2 * index / segmentCount;
            var endAngle = Math.PI * 2 * (index + 1.2) / segmentCount;
            var path = new PathF();
            path.MoveTo(center);
            path.LineTo(PointOnCircle(center, radius, startAngle));
            path.LineTo(PointOnCircle(center, radius, endAngle));
            path.Close();

            canvas.FillColor = Color.FromHsla(index / (double)segmentCount, 1, 0.5);
            canvas.FillPath(path);
        }

        for (var layer = 12; layer >= 1; layer--)
        {
            canvas.FillColor = Colors.White.WithAlpha(0.075f);
            canvas.FillCircle(center, radius * layer / 12);
        }
    }

    private void DrawSelectionHandles(
        ICanvas canvas,
        PointF center,
        float ringInnerRadius,
        float outerRadius,
        float colorRadius)
    {
        var hueAngle = Hue * Math.PI * 2;
        var colorPoint = PointOnCircle(center, colorRadius * Saturation, hueAngle);
        DrawHandle(canvas, colorPoint, SelectedColor());

        var temperatureAngle = DegreesToRadians(135 + Temperature * 270);
        var temperatureRadius = (ringInnerRadius + outerRadius) / 2;
        var temperaturePoint = PointOnCircle(center, temperatureRadius, temperatureAngle);
        DrawHandle(
            canvas,
            temperaturePoint,
            Interpolate(Color.FromArgb("#FFB36B"), Color.FromArgb("#D9ECFF"), Temperature));
    }

    private static void DrawHandle(ICanvas canvas, PointF point, Color fill)
    {
        canvas.FillColor = fill;
        canvas.FillCircle(point, 9);
        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = 3;
        canvas.DrawCircle(point, 10);
    }

    private Color SelectedColor() => Color.FromHsla(Hue, Saturation, 0.52);

    private static PointF PointOnCircle(PointF center, double radius, double angle) =>
        new(
            center.X + (float)(Math.Cos(angle) * radius),
            center.Y + (float)(Math.Sin(angle) * radius));

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static Color Interpolate(Color start, Color end, double amount) =>
        new(
            (float)(start.Red + (end.Red - start.Red) * amount),
            (float)(start.Green + (end.Green - start.Green) * amount),
            (float)(start.Blue + (end.Blue - start.Blue) * amount),
            1);
}
