using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>
/// A body imported from another CAD format (STEP, IGES, BRep), stored in the
/// part as OCCT BRep text so the part file is self-contained. It has no
/// feature history of its own: later features can still fillet, cut or
/// pattern it.
/// </summary>
public sealed class ImportFeature : Feature
{
    public override string DefaultName => "Imported";

    /// <summary>Original file name, for display.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>The geometry in OCCT BRep text format.</summary>
    public string Brep { get; set; } = string.Empty;

    public static ImportFeature FromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ext = Path.GetExtension(path).ToUpperInvariant();
        using var shape = ext switch
        {
            ".STEP" or ".STP" => Shape.ReadStep(path),
            ".IGES" or ".IGS" => Shape.ReadIges(path),
            ".BREP" or ".BRP" => Shape.ReadBrep(path),
            _ => throw new NotSupportedException($"Can't import {ext} files."),
        };
        return new ImportFeature { SourceName = Path.GetFileName(path), Brep = shape.ToBrep() };
    }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var shape = Shape.FromBrep(Brep);
        if (shape.FaceCount == 0)
        {
            throw new FeatureException("The imported file has no faces.");
        }

        var names = new string[shape.FaceCount];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = $"{Id}:face{i + 1}";
        }

        ctx.Bodies.Add(new Body(shape, names, string.IsNullOrEmpty(SourceName) ? $"Body{ctx.Bodies.Count + 1}" : Path.GetFileNameWithoutExtension(SourceName)));
    }
}
