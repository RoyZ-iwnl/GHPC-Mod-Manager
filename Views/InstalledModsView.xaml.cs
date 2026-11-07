using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GHPC_Mod_Manager.Views;

public partial class InstalledModsView : UserControl
{
    private ModViewModel? _draggedMod;
    private Point _dragStartPoint;
    private Popup? _dragPreviewPopup;
    private FrameworkElement? _dragPreviewSource;

    public InstalledModsView()
    {
        InitializeComponent();
    }

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModViewModel mod })
            return;

        _draggedMod = mod;
        _dragStartPoint = e.GetPosition(InstalledModsItems);
    }

    private void InstalledModsItems_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedMod == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPosition = e.GetPosition(InstalledModsItems);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var draggedMod = _draggedMod;
        var sourceCard = FindCard(draggedMod);
        if (sourceCard == null)
            return;

        var positions = CaptureCardPositions();
        _draggedMod = null;
        ShowDragPreview(sourceCard);
        InstalledModsItems.GiveFeedback += InstalledModsItems_GiveFeedback;
        try
        {
            DragDrop.DoDragDrop(InstalledModsItems, draggedMod, DragDropEffects.Move);
        }
        finally
        {
            InstalledModsItems.GiveFeedback -= InstalledModsItems_GiveFeedback;
            HideDragPreview();
            InstalledModsItems.UpdateLayout();
            AnimateReorderedCards(positions);
        }
    }

    private void InstalledModsItems_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedMod = null;
    }

    private void ModCard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ModViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Move && sender is Border card)
        {
            var before = e.GetPosition(card).Y < card.ActualHeight / 2;
            card.BorderBrush = (Brush)FindResource("AccentBrush");
            card.BorderThickness = before
                ? new Thickness(1, 3, 1, 1)
                : new Thickness(1, 1, 1, 3);
        }
        e.Handled = true;
    }

    private void ModCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border card)
        {
            card.ClearValue(Border.BorderBrushProperty);
            card.ClearValue(Border.BorderThicknessProperty);
        }
    }

    private async void ModCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModViewModel target } card ||
            e.Data.GetData(typeof(ModViewModel)) is not ModViewModel source ||
            DataContext is not InstalledModsViewModel viewModel)
            return;

        if (card is Border targetCard)
        {
            targetCard.ClearValue(Border.BorderBrushProperty);
            targetCard.ClearValue(Border.BorderThicknessProperty);
        }

        var insertAfter = e.GetPosition(card).Y >= card.ActualHeight / 2;
        if (!viewModel.ReorderInstalledMod(source, target, insertAfter))
            return;

        e.Handled = true;
        await Dispatcher.InvokeAsync(InstalledModsItems.UpdateLayout, DispatcherPriority.Render);
        await viewModel.SaveInstalledModOrderAsync();
    }

    private FrameworkElement? FindCard(ModViewModel mod)
    {
        for (var i = 0; i < InstalledModsItems.Items.Count; i++)
        {
            if (InstalledModsItems.Items[i] is ModViewModel item && ReferenceEquals(item, mod))
                return InstalledModsItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
        }

        return null;
    }

    private void ShowDragPreview(FrameworkElement card)
    {
        if (_dragPreviewPopup != null)
            return;

        InstalledModsItems.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(card.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(card.ActualHeight));
        var snapshot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(card);
        var preview = new Border
        {
            Width = width,
            Height = height,
            Opacity = 0.92,
            IsHitTestVisible = false,
            Child = new Image
            {
                Source = snapshot,
                Stretch = Stretch.Fill
            }
        };
        _dragPreviewSource = card;
        card.Opacity = 0.45;

        _dragPreviewPopup = new Popup
        {
            Child = preview,
            PlacementTarget = card,
            Placement = PlacementMode.Mouse,
            AllowsTransparency = true,
            IsHitTestVisible = false,
            IsOpen = true
        };
    }

    private void InstalledModsItems_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_dragPreviewPopup == null)
            return;

        _dragPreviewPopup.HorizontalOffset = 16;
        _dragPreviewPopup.VerticalOffset = 16;
        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.SizeAll);
        e.Handled = true;
    }

    private void HideDragPreview()
    {
        if (_dragPreviewPopup == null)
            return;

        _dragPreviewPopup.IsOpen = false;
        _dragPreviewPopup = null;
        if (_dragPreviewSource != null)
            _dragPreviewSource.Opacity = 1;
        _dragPreviewSource = null;
    }

    private Dictionary<ModViewModel, double> CaptureCardPositions()
    {
        var positions = new Dictionary<ModViewModel, double>();
        for (var i = 0; i < InstalledModsItems.Items.Count; i++)
        {
            if (InstalledModsItems.Items[i] is not ModViewModel mod ||
                InstalledModsItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            positions[mod] = container.TranslatePoint(new Point(), InstalledModsItems).Y;
        }

        return positions;
    }

    private void AnimateReorderedCards(Dictionary<ModViewModel, double> oldPositions)
    {
        for (var i = 0; i < InstalledModsItems.Items.Count; i++)
        {
            if (InstalledModsItems.Items[i] is not ModViewModel mod ||
                !oldPositions.TryGetValue(mod, out var oldY) ||
                InstalledModsItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            var newY = container.TranslatePoint(new Point(), InstalledModsItems).Y;
            var offset = oldY - newY;
            if (Math.Abs(offset) < 0.5)
                continue;

            var transform = new TranslateTransform(0, offset);
            container.RenderTransform = transform;
            var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) => container.RenderTransform = Transform.Identity;
            transform.BeginAnimation(TranslateTransform.YProperty, animation);
        }
    }
}
