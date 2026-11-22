using Chamfer.Assembly;
using Chamfer.App.Part;
using Chamfer.Geometry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>
/// Dragging components in the 3D view (Drag mode): a pressed component follows the mouse across
/// the view plane, and the mates are solved as it goes, so a mated component only slides the
/// ways it still can.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    public partial bool DragComponents { get; set; }

    [RelayCommand]
    private void ToggleDragComponents()
    {
        DragComponents = !DragComponents;
        MessageText = DragComponents ? "Drag components with the left mouse button; mates hold them as they move." : string.Empty;
    }

    /// <summary>The component being dragged, where it started, and the grab point on the view plane.</summary>
    private (Component Component, Transform3 Start, Vec3 Grab, Vec3 Normal, bool Begun)? _drag;

    /// <summary>Called when the left button goes down on a pick: starts a drag on a free component in Drag mode.</summary>
    private bool BeginComponentDrag(int id)
    {
        _drag = null;
        if (!DragComponents || AssemblyDoc is null || AssemblyBuilder.Target(id) is not ComponentPick pick || pick.Component.Fixed)
        {
            return false;
        }

        _drag = (pick.Component, pick.Component.Transform, Vec3.Zero, Vec3.Zero, false);
        return true;
    }

    /// <summary>Moves the dragged component to follow the mouse ray, then solves the mates.</summary>
    public void DragComponent(Vec3 rayOrigin, Vec3 rayDirection)
    {
        if (_drag is not { } d || AssemblyDoc is not { } asm)
        {
            return;
        }

        var dir = rayDirection.Normalized();
        if (!d.Begun)
        {
            // The grab point: where the first ray meets the plane through the component's centre, facing the view.
            SnapshotAssembly();
            var center = AssemblyTools.Center(asm, d.Component);
            var grab = rayOrigin + (dir * Vec3.Dot(center - rayOrigin, dir));
            _drag = d = (d.Component, d.Component.Transform, grab, dir, true);
            return;
        }

        var along = Vec3.Dot(dir, d.Normal);
        if (Math.Abs(along) < 1e-9)
        {
            return;
        }

        var hit = rayOrigin + (dir * (Vec3.Dot(d.Grab - rayOrigin, d.Normal) / along));
        d.Component.Transform = Transform3.Translation(hit - d.Grab) * d.Start;
        _lastSolve = asm.Solve();
        RaiseScene();
    }
}
