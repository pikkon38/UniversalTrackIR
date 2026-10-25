using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using UniversalTrackIR.ViewModels;

namespace UniversalTrackIR;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        // Title shows the build stamp so you can confirm an update landed.
        string? version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(version)) Title = $"Universal TrackIR  v{version}";

        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _vm.AttachWindow(new WindowInteropHelper(this).Handle);
    }

    /// <summary>While a hotkey button is waiting, the next key press becomes the hotkey.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_vm.IsCapturingHotkey)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_vm.OnCaptureKey(key, Keyboard.Modifiers))
            {
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Scroll over a slider: one small step per notch, a tenth of that with Shift.</summary>
    private void Slider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider) return;
        bool fine = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        double step = slider.SmallChange * (fine ? 0.1 : 1);
        if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0) step = Math.Max(step, slider.TickFrequency);

        double value = Math.Clamp(slider.Value + Math.Sign(e.Delta) * step, slider.Minimum, slider.Maximum);
        slider.SetCurrentValue(RangeBase.ValueProperty, value);
        e.Handled = true;
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm.IsRenaming) _vm.CommitRenameCommand.Execute(null);
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
        {
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Dispose();
        base.OnClosing(e);
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
