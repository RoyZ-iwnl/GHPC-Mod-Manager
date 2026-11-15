using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace GHPC_Mod_Manager.Views.Controls;

public sealed class SpeedGraphControl : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(INotifyCollectionChanged), typeof(SpeedGraphControl),
        new FrameworkPropertyMetadata(null, OnSamplesChanged));

    public INotifyCollectionChanged? Samples
    {
        get => (INotifyCollectionChanged?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SpeedGraphControl control) return;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= control.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += control.OnCollectionChanged;
        control.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var background = TryFindResource("SurfaceContainerBrush") as Brush ?? Brushes.Transparent;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        drawingContext.DrawRoundedRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight), 4, 4);

        var values = DataContext is ViewModels.DownloadManagerViewModel vm ? vm.SpeedSamples.ToArray() : Array.Empty<double>();
        if (values.Length < 2 || ActualWidth < 2 || ActualHeight < 2) return;
        var max = Math.Max(values.Max(), 1d);
        var points = values.Select((value, index) => new Point(
            index * (ActualWidth - 1) / (values.Length - 1),
            ActualHeight - 4 - (Math.Clamp(value / max, 0, 1) * (ActualHeight - 8)))).ToArray();
        var pen = new Pen(accent, 2);
        pen.Freeze();
        for (var i = 1; i < points.Length; i++)
            drawingContext.DrawLine(pen, points[i - 1], points[i]);
    }
}
