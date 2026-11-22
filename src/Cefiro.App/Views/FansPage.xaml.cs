using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cefiro.App.ViewModels;

namespace Cefiro.App.Views;

public partial class FansPage : UserControl
{
    private FansViewModel? _vm;

    public FansPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;

        _vm = e.NewValue as FansViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FansViewModel.Editing))
            AnimateEditor(_vm?.Editing is not null);
    }

    /// <summary>El editor aparece con un fundido y un ligero zoom, y desaparece igual.</summary>
    private void AnimateEditor(bool open)
    {
        var duration = TimeSpan.FromMilliseconds(open ? 220 : 160);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (open)
        {
            EditorLayer.Visibility = Visibility.Visible;
            Focus();
        }

        var fade = new DoubleAnimation(open ? 1 : 0, duration) { EasingFunction = ease };
        if (!open)
            fade.Completed += (_, _) =>
            {
                if (_vm?.Editing is null)
                    EditorLayer.Visibility = Visibility.Collapsed;
            };
        EditorLayer.BeginAnimation(OpacityProperty, fade);

        var from = open ? 0.96 : 1.0;
        var to = open ? 1.0 : 0.97;
        EditorScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease });
        EditorScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease });
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        _vm?.CloseEditorCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm?.Editing is not null && Keyboard.FocusedElement is not TextBox)
        {
            _vm.CloseEditorCommand.Execute(null);
            e.Handled = true;
        }
    }
}
