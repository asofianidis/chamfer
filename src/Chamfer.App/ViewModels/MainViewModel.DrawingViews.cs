using Chamfer.Drawing;
using Chamfer.Geometry;

namespace Chamfer.App.ViewModels;

/// <summary>Projected, auxiliary, cropped and broken drawing views.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Runs a drawing edit in a panel: preview while editing, one undo step on OK, restore on Cancel.</summary>
    private FeatureEditor DrawingPanel(string title, DrawingDocument d, Action apply)
    {
        var before = d.Save();
        return FeatureEditor.Command(title, apply,
            accept: () =>
            {
                _undo.Push("drw:" + before);
                _dirty = true;
                UndoCommand.NotifyCanExecuteChanged();
            },
            cancel: () => RestoreDrawing(before));
    }

    private (Point2 Min, Point2 Max) ViewExtent(Sheet sheet, DrawingView view)
    {
        var pts = _drawingRenderer!.Geometry(sheet, view).Lines.SelectMany(l => l.Points).ToList();
        return pts.Count == 0
            ? (new Point2(0, 0), new Point2(10, 10))
            : (new Point2(pts.Min(p => p.X), pts.Min(p => p.Y)), new Point2(pts.Max(p => p.X), pts.Max(p => p.Y)));
    }

    /// <summary>A view's extent without its crop or break (which stay as they are).</summary>
    private (Point2 Min, Point2 Max) UncutExtent(Sheet sheet, DrawingView view)
    {
        var (crop, cut) = (view.Crop, view.Break);
        (view.Crop, view.Break) = (null, null);
        try
        {
            return ViewExtent(sheet, view);
        }
        finally
        {
            (view.Crop, view.Break) = (crop, cut);
        }
    }

    private void StartDimensionProperties()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet ||
            sheet.Dimensions.Find(x => DrawingSelection.Contains(x.Id)) is not { } dim)
        {
            MessageText = "Select a dimension.";
            return;
        }

        CancelEditor();
        var text = new TextField("Text (blank: the measured value)", dim.Text ?? string.Empty, _ => { });
        var tolerance = new TextField("Tolerance (e.g. ±0.1)", dim.Tolerance ?? string.Empty, _ => { });
        string[] fits = ["None", .. Fits.Common];
        var fit = Math.Max(0, Array.IndexOf(fits, dim.Fit ?? "None"));
        var limits = dim.ShowLimits;
        void Apply()
        {
            dim.Text = string.IsNullOrWhiteSpace(text.Text) ? null : text.Text.Trim();
            dim.Tolerance = string.IsNullOrWhiteSpace(tolerance.Text) ? null : tolerance.Text.Trim();
            dim.Fit = fit == 0 ? null : fits[fit];
            dim.ShowLimits = limits;
            RefreshDrawing();
        }

        var before = d.Save();
        var panel = FeatureEditor.Command("Dimension", Apply,
            accept: () =>
            {
                Apply(); // typed text counts even without Enter
                _undo.Push("drw:" + before);
                _dirty = true;
                UndoCommand.NotifyCanExecuteChanged();
            },
            cancel: () => RestoreDrawing(before));
        panel.AddInfo(new InfoField("Measured", Units.Format(_drawingRenderer!.Measure(sheet, dim), d.LengthUnit)));
        panel.AddField(text);
        panel.AddField(tolerance);
        panel.AddField(new ChoiceField("Fit (ISO 286)", fits, fit, i =>
        {
            fit = i;
            Apply();
        }));
        panel.AddField(new BoolField("Show the fit's limits", limits, v =>
        {
            limits = v;
            Apply();
        }));
        Editor = panel;
    }

    private void StartDimensionSet()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } view || _drawingRenderer is null)
        {
            MessageText = "Select a view to dimension.";
            return;
        }

        if (view.Kind is ViewKind.Isometric or ViewKind.Section or ViewKind.Detail)
        {
            MessageText = "Dimension sets work on orthographic views (front, top, right...).";
            return;
        }

        CancelEditor();
        var kind = DimensionSetKind.Baseline;
        var vertical = false;
        var made = new List<DrawingDimension>();
        void Apply()
        {
            foreach (var m in made)
            {
                sheet.Dimensions.Remove(m);
            }

            made = [.. DimensionSets.Add(d, sheet, view, _drawingRenderer.Geometry(sheet, view), kind, vertical)];
            RefreshDrawing();
        }

        var panel = DrawingPanel("Dimension Set", d, Apply);
        panel.AddField(new ChoiceField("Type", ["Baseline", "Chain", "Ordinate"], 0, i =>
        {
            kind = (DimensionSetKind)i;
            Apply();
        }));
        panel.AddField(new ChoiceField("Direction", ["Horizontal", "Vertical"], 0, i =>
        {
            vertical = i == 1;
            Apply();
        }));
        panel.AddInfo(new InfoField("Measures", "Hole centres and the far edge, from the view's lower-left corner."));
        Editor = panel;
        Apply();
    }

    private void StartProjectedView()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } parent || _drawingRenderer is null)
        {
            MessageText = "Select the view to project from.";
            return;
        }

        CancelEditor();
        var side = ViewSide.Right;
        DrawingView? view = null;
        var (min, max) = ViewExtent(sheet, parent);
        var size = Math.Max(max.X - min.X, max.Y - min.Y) * (parent.Scale ?? sheet.Scale);
        void Apply()
        {
            if (view is not null)
            {
                sheet.Views.Remove(view);
            }

            var offset = size + 30;
            var (x, y) = side switch
            {
                ViewSide.Right => (parent.X + offset, parent.Y),
                ViewSide.Left => (parent.X - offset, parent.Y),
                ViewSide.Above => (parent.X, parent.Y + offset),
                _ => (parent.X, parent.Y - offset),
            };
            view = d.AddProjected(sheet, parent, side, x, y);
            RefreshDrawing();
        }

        var panel = DrawingPanel("Projected View", d, Apply);
        panel.AddField(new ChoiceField("Side", ["Right", "Left", "Above", "Below"], 0, i =>
        {
            side = (ViewSide)i;
            Apply();
        }));
        Editor = panel;
        Apply();
    }

    private void StartAuxiliaryView()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } parent || _drawingRenderer is null)
        {
            MessageText = "Select the view with the inclined edge.";
            return;
        }

        CancelEditor();
        var angle = 45.0;
        DrawingView? view = null;
        var (min, max) = ViewExtent(sheet, parent);
        var center = new Point2((min.X + max.X) / 2, (min.Y + max.Y) / 2);
        void Apply()
        {
            if (view is not null)
            {
                sheet.Views.Remove(view);
            }

            var dir = new Point2(Math.Cos(angle * Math.PI / 180), Math.Sin(angle * Math.PI / 180));
            var normal = new Point2(-dir.Y, dir.X);
            var away = ((max.X - min.X) + (max.Y - min.Y)) * (parent.Scale ?? sheet.Scale);
            view = d.AddAuxiliary(sheet, parent, center, center + dir, parent.X + (normal.X * away), parent.Y + (normal.Y * away));
            PlaceInFreeSpace(sheet, view, parent);
            RefreshDrawing();
        }

        var panel = DrawingPanel("Auxiliary View", d, Apply);
        panel.AddField(new NumberField("Edge angle in the view", "°", () => angle, v =>
        {
            angle = v;
            Apply();
        }));
        panel.AddInfo(new InfoField("How", "Give the angle of the inclined edge as it appears in the selected view; the new view looks square to it."));
        Editor = panel;
        Apply();
    }

    private void StartCropView()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } view || _drawingRenderer is null)
        {
            MessageText = "Select the view to crop.";
            return;
        }

        CancelEditor();
        var (min, max) = UncutExtent(sheet, view);
        double[] crop = view.Crop is [_, _, _, _] old ? [.. old] : [min.X, min.Y, (min.X + max.X) / 2, max.Y];
        void Apply()
        {
            view.Crop = [.. crop];
            RefreshDrawing();
        }

        var panel = DrawingPanel("Crop View", d, Apply);
        string[] labels = ["Left", "Bottom", "Right", "Top"];
        for (var i = 0; i < 4; i++)
        {
            var k = i;
            panel.AddField(new NumberField(labels[i], "mm", () => crop[k], v =>
            {
                crop[k] = v;
                Apply();
            }));
        }

        panel.AddField(new ButtonField("Remove crop", new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
        {
            view.Crop = null;
            RefreshDrawing();
        })));
        panel.AddInfo(new InfoField("Coordinates", "Model millimetres in the view's own directions."));
        Editor = panel;
        Apply();
    }

    private void StartBreakView()
    {
        if (DrawingDoc is not { } d || CurrentSheet is not { } sheet || SelectedView() is not { } view || _drawingRenderer is null)
        {
            MessageText = "Select the view to break.";
            return;
        }

        CancelEditor();
        var (min, max) = UncutExtent(sheet, view);
        var alongY = view.Break is null ? max.Y - min.Y > max.X - min.X : view.BreakAlongY;
        double from = 0, to = 0;
        void Reset()
        {
            var (lo, hi) = alongY ? (min.Y, max.Y) : (min.X, max.X);
            (from, to) = (lo + ((hi - lo) / 3), lo + ((hi - lo) * 2 / 3));
        }

        void Apply()
        {
            view.BreakAlongY = alongY;
            view.Break = [from, to];
            RefreshDrawing();
        }

        Reset();
        if (view.Break is [var b0, var b1])
        {
            (from, to) = (b0, b1);
        }

        var panel = DrawingPanel("Break View", d, Apply);
        NumberField? fromField = null, toField = null;
        panel.AddField(new BoolField("Break a tall view (across Y)", alongY, v =>
        {
            alongY = v;
            Reset();
            fromField?.Reload();
            toField?.Reload();
            Apply();
        }));
        fromField = new NumberField("Leave out from", "mm", () => from, v =>
        {
            from = v;
            Apply();
        });
        toField = new NumberField("to", "mm", () => to, v =>
        {
            to = v;
            Apply();
        });
        panel.AddField(fromField);
        panel.AddField(toField);
        panel.AddField(new ButtonField("Remove break", new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
        {
            view.Break = null;
            RefreshDrawing();
        })));
        Editor = panel;
        Apply();
    }
}
