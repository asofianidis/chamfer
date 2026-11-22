using System.Collections.ObjectModel;
using System.Globalization;
using Chamfer.App.Editing;
using Chamfer.App.Part;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>One row in the property panel.</summary>
public abstract partial class PropertyField(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}

/// <summary>A length, angle or count typed as an expression ("25", "1in", "10*3").</summary>
public sealed partial class NumberField : PropertyField
{
    private readonly Action<double> _apply;
    private readonly Func<double> _read;

    public NumberField(string label, string unit, Func<double> read, Action<double> apply, double min = double.NegativeInfinity, bool wholeNumber = false)
        : base(label)
    {
        Unit = unit;
        _read = read;
        _apply = apply;
        Min = min;
        IsInteger = wholeNumber;
        Text = Format(read());
        CommitCommand = new RelayCommand(Commit);
    }

    public string Unit { get; }
    public double Min { get; }
    public bool IsInteger { get; }
    public IRelayCommand CommitCommand { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    private string Format(double v) => v.ToString(IsInteger ? "0" : "0.####", CultureInfo.InvariantCulture);

    /// <summary>Parses the text and applies it; restores the old value if it doesn't parse.</summary>
    public void Commit()
    {
        double value;
        if (Unit == "°")
        {
            if (!double.TryParse(Text.Replace("°", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !ValueParser.TryParse(Text, out value))
            {
                HasError = true;
                return;
            }
        }
        else if (!ValueParser.TryParse(Text, out value))
        {
            HasError = true;
            return;
        }

        if (IsInteger)
        {
            value = Math.Round(value);
        }

        if (value < Min)
        {
            HasError = true;
            return;
        }

        HasError = false;
        if (Math.Abs(value - _read()) > 1e-12)
        {
            _apply(value);
        }

        Text = Format(_read());
    }
}

/// <summary>A drop-down choice.</summary>
public sealed partial class ChoiceField : PropertyField
{
    private readonly Action<int> _apply;
    private readonly bool _ready;

    public ChoiceField(string label, IReadOnlyList<string> options, int index, Action<int> apply)
        : base(label)
    {
        Options = options;
        _apply = apply;
        Index = index;
        _ready = true;
    }

    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    public partial int Index { get; set; }

    partial void OnIndexChanged(int value)
    {
        if (_ready && value >= 0)
        {
            _apply(value);
        }
    }
}

/// <summary>A check box.</summary>
public sealed partial class BoolField : PropertyField
{
    private readonly Action<bool> _apply;
    private readonly bool _ready;

    public BoolField(string label, bool value, Action<bool> apply)
        : base(label)
    {
        _apply = apply;
        Value = value;
        _ready = true;
    }

    [ObservableProperty]
    public partial bool Value { get; set; }

    partial void OnValueChanged(bool value)
    {
        if (_ready)
        {
            _apply(value);
        }
    }
}

/// <summary>
/// A list of selected model entities. While active, it mirrors the viewport
/// selection (filtered to the kinds it accepts).
/// </summary>
public sealed partial class SelectionField : PropertyField
{
    private readonly Action<IReadOnlyList<SelectionItem>> _apply;

    public SelectionField(string label, IReadOnlyList<SelectionKind> accepts, IEnumerable<SelectionItem> initial, Action<IReadOnlyList<SelectionItem>> apply, int max = int.MaxValue)
        : base(label)
    {
        Accepts = accepts;
        _apply = apply;
        Max = max;
        foreach (var i in initial)
        {
            Items.Add(i);
        }

        Prompt = $"Select {string.Join(" or ", accepts.Select(a => a.ToString().ToLowerInvariant() + (max > 1 ? "s" : string.Empty)))}";
    }

    public IReadOnlyList<SelectionKind> Accepts { get; }
    public int Max { get; }
    public ObservableCollection<SelectionItem> Items { get; } = [];
    public string Prompt { get; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Set by the editor so clicking the field makes it the active one.</summary>
    public IRelayCommand? ActivateCommand { get; set; }

    public bool Accept(SelectionItem item) => Accepts.Contains(item.Kind);

    /// <summary>Replaces the items with the accepted part of a selection.</summary>
    public void Sync(IReadOnlyList<SelectionItem> selection)
    {
        var accepted = selection.Where(Accept).TakeLast(Max).ToList();
        if (accepted.Count == Items.Count && accepted.Zip(Items).All(p => p.First.SameTarget(p.Second)))
        {
            return;
        }

        Items.Clear();
        foreach (var i in accepted)
        {
            Items.Add(i);
        }

        _apply(accepted);
    }
}

/// <summary>Free text, applied on Enter or when focus leaves.</summary>
public sealed partial class TextField : PropertyField
{
    private readonly Action<string> _apply;

    public TextField(string label, string text, Action<string> apply)
        : base(label)
    {
        _apply = apply;
        Text = text;
        CommitCommand = new RelayCommand(Commit);
    }

    public IRelayCommand CommitCommand { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    public void Commit()
    {
        if (!string.IsNullOrWhiteSpace(Text))
        {
            _apply(Text.Trim());
        }
    }
}

/// <summary>A read-only line of text (hints, results).</summary>
public sealed partial class InfoField(string label, string text) : PropertyField(label)
{
    [ObservableProperty]
    public partial string Text { get; set; } = text;
}
