using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;

namespace Chamfer.App.Editing;

/// <summary>Temporary geometry a tool draws while the user is still placing it.</summary>
public abstract record PreviewItem;

public sealed record PreviewLine(Vec2 A, Vec2 B, bool Construction = false) : PreviewItem;

public sealed record PreviewCircle(Vec2 Center, double Radius, bool Construction = false) : PreviewItem;

/// <summary>Counter-clockwise arc from <paramref name="StartAngle"/> through <paramref name="Sweep"/> radians.</summary>
public sealed record PreviewArc(Vec2 Center, double Radius, double StartAngle, double Sweep) : PreviewItem;

/// <summary>A glyph next to a point, such as an inferred relation ("H", "V", "●").</summary>
public sealed record PreviewGlyph(Vec2 At, string Text) : PreviewItem;

/// <summary>A selection rectangle. Crossing boxes select anything they touch; window boxes only what's inside.</summary>
public sealed record PreviewBox(Vec2 Corner1, Vec2 Corner2, bool Crossing) : PreviewItem;

/// <summary>A dimension that is being placed but isn't in the sketch yet.</summary>
public sealed record PreviewDimension(DimensionConstraint Dimension) : PreviewItem;
