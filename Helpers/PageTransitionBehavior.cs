using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace GHPC_Mod_Manager.Helpers;

public class TransitionContentControl : ContentControl
{
    public TransitionContentControl()
    {
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlayTransition();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        // 当 Content 变化时，播放过渡动画
        Dispatcher.InvokeAsync(PlayTransition);
    }

    private void PlayTransition()
    {
        var storyboard = TryFindResource("EndfieldSubPageSlideIn") as Storyboard
                      ?? Application.Current.TryFindResource("EndfieldSubPageSlideIn") as Storyboard;

        if (storyboard != null)
        {
            if (RenderTransform is not TranslateTransform)
            {
                RenderTransform = new TranslateTransform();
            }
            storyboard.Begin(this);
        }
    }
}

public static class PageTransitionBehavior
{
    public static readonly DependencyProperty IsMainTransitionProperty =
        DependencyProperty.RegisterAttached(
            "IsMainTransition",
            typeof(bool),
            typeof(PageTransitionBehavior),
            new PropertyMetadata(false, OnIsMainTransitionChanged));

    public static bool GetIsMainTransition(DependencyObject obj)
        => (bool)obj.GetValue(IsMainTransitionProperty);

    public static void SetIsMainTransition(DependencyObject obj, bool value)
        => obj.SetValue(IsMainTransitionProperty, value);

    private static void OnIsMainTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentPresenter presenter) return;

        if ((bool)e.NewValue)
        {
            presenter.Loaded += OnPresenterLoaded;
        }
    }

    private static void OnPresenterLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentPresenter presenter)
        {
            presenter.Loaded -= OnPresenterLoaded;
            PlayMainPageTransition(presenter);
        }
    }

    private static void PlayMainPageTransition(ContentPresenter? presenter)
    {
        if (presenter == null) return;

        var storyboard = presenter.TryFindResource("EndfieldPageSlideIn") as Storyboard
                      ?? Application.Current.TryFindResource("EndfieldPageSlideIn") as Storyboard;

        if (storyboard != null)
        {
            if (presenter.RenderTransform is not TranslateTransform)
            {
                presenter.RenderTransform = new TranslateTransform();
            }
            storyboard.Begin(presenter);
        }
    }
}
