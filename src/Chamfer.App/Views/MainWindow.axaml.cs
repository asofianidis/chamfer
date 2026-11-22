using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Chamfer.App.Editing;
using Chamfer.App.ViewModels;
using Chamfer.Sketching.Constraints;

namespace Chamfer.App.Views;

public partial class MainWindow : Window
{
    private DimensionConstraint? _editing;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        DimensionInput.KeyDown += OnDimensionInputKeyDown;
        DimensionInput.LostFocus += (_, _) => HideDimensionInput();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.StorageProvider = StorageProvider;
        vm.ZoomToFitAction = Canvas.ZoomToFit;
        vm.Editor.DimensionEditRequested += (_, dim) => ShowDimensionInput(dim);
        Canvas.CursorMoved += (_, world) =>
        {
            vm.SetCursor(world);
            vm.RefreshHint();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Canvas.Focus();
    }

    private static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        var command = e.KeyModifiers.HasFlag(CommandModifier);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (command)
        {
            switch (e.Key)
            {
                case Key.Z when shift:
                case Key.Y:
                    vm.RedoCommand.Execute(null);
                    break;
                case Key.Z:
                    vm.UndoCommand.Execute(null);
                    break;
                case Key.S when shift:
                    vm.SaveAsCommand.Execute(null);
                    break;
                case Key.S:
                    vm.SaveCommand.Execute(null);
                    break;
                case Key.O:
                    vm.OpenCommand.Execute(null);
                    break;
                case Key.N:
                    vm.NewCommand.Execute(null);
                    break;
                default:
                    return;
            }

            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None && e.KeyModifiers != KeyModifiers.Shift)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                vm.Escape();
                break;
            case Key.Enter:
                vm.Editor.Tool.Finish();
                vm.Editor.Invalidate();
                break;
            case Key.Delete:
            case Key.Back:
                vm.DeleteCommand.Execute(null);
                break;
            case Key.F:
                Canvas.ZoomToFit();
                break;
            case Key.X:
                vm.ToggleConstructionCommand.Execute(null);
                break;
            default:
                var tool = vm.Tools.FirstOrDefault(t => t.Shortcut.Length == 1 && t.Shortcut[0] == e.Key.ToString()[0] && e.Key.ToString().Length == 1);
                if (tool is null)
                {
                    return;
                }

                tool.Activate.Execute(null);
                break;
        }

        e.Handled = true;
    }

    private void ShowDimensionInput(DimensionConstraint dim)
    {
        _editing = dim;
        var label = Canvas.ToScreen(DimensionGeometry.Layout(dim).Label);
        var value = dim.IsAngular ? dim.Value * 180 / Math.PI : dim.Value;
        DimensionInput.Text = value.ToString("0.####", CultureInfo.InvariantCulture);
        Avalonia.Controls.Canvas.SetLeft(DimensionInput, Math.Clamp(label.X - DimensionInput.Width / 2, 4, Math.Max(4, Overlay.Bounds.Width - DimensionInput.Width - 4)));
        Avalonia.Controls.Canvas.SetTop(DimensionInput, Math.Clamp(label.Y + 14, 4, Math.Max(4, Overlay.Bounds.Height - 40)));
        DimensionInput.IsVisible = true;
        DimensionInput.Focus();
        DimensionInput.SelectAll();
    }

    private void HideDimensionInput()
    {
        _editing = null;
        DimensionInput.IsVisible = false;
        Canvas.Focus();
    }

    private void OnDimensionInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideDimensionInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _editing is { } dim && ViewModel is { } vm)
        {
            e.Handled = true;
            if (!ValueParser.TryParse(DimensionInput.Text, out var value))
            {
                vm.Editor.SetMessage("Enter a number, for example 25, 25.4*2 or 1in.");
                return;
            }

            if (vm.Editor.SetDimensionValue(dim, value))
            {
                HideDimensionInput();
            }
        }
    }
}
