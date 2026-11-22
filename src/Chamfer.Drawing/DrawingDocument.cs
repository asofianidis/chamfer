using System.Text.Json;
using System.Text.Json.Serialization;
using Chamfer.Assembly;
using Chamfer.Document;
using Chamfer.Geometry;

namespace Chamfer.Drawing;

public enum SheetSize
{
    A4,
    A3,
    A2,
    A1,
    A0,
    Letter,
    Tabloid,
}

public enum ViewKind
{
    Front,
    Top,
    Right,
    Left,
    Back,
    Bottom,
    Isometric,
    Section,
    Detail,

    /// <summary>Projected from a parent view to one side (third-angle).</summary>
    Projected,

    /// <summary>Looking square to an edge of a parent view (true size of an inclined face).</summary>
    Auxiliary,
}

/// <summary>Which side of its parent a projected view is placed (third-angle projection).</summary>
public enum ViewSide
{
    Right,
    Left,
    Above,
    Below,
}

/// <summary>Where a dimension end attaches: an edge end, midpoint or centre (follows the model), or a fixed model point.</summary>
public sealed record DimensionAnchor(TopoRef? Edge, int Which, Vec3 Point)
{
    public const int Start = 0;
    public const int End = 1;
    public const int Center = 2;
}

public enum DimensionKind
{
    Horizontal,
    Vertical,
    Aligned,
    Diameter,
    Radius,

    /// <summary>B's distance along X from the datum A, written at the end of a leader.</summary>
    OrdinateHorizontal,

    /// <summary>B's distance along Y from the datum A.</summary>
    OrdinateVertical,
}

/// <summary>A dimension in a view, measured on the projected model.</summary>
public sealed class DrawingDimension
{
    public string Id { get; set; } = string.Empty;

    public string? Layer { get; set; }

    public string ViewId { get; set; } = string.Empty;

    public DimensionKind Kind { get; set; }

    public DimensionAnchor A { get; set; } = new(null, 0, Vec3.Zero);

    /// <summary>Second end; unused for diameters and radii (they use A's edge).</summary>
    public DimensionAnchor? B { get; set; }

    /// <summary>Distance of the dimension line from the measured points, in sheet mm (sign picks the side).</summary>
    public double Offset { get; set; } = 10;

    /// <summary>Text override; null shows the measured value.</summary>
    public string? Text { get; set; }

    /// <summary>Tolerance text appended to the value, e.g. "±0.1".</summary>
    public string? Tolerance { get; set; }
}

/// <summary>A named drawing layer: annotations on it share a colour and can be hidden together.</summary>
public sealed class DrawingLayer
{
    public string Name { get; set; } = "Annotations";

    /// <summary>Colour as 0xRRGGBB.</summary>
    public uint Color { get; set; }

    public bool Visible { get; set; } = true;
}

/// <summary>Drafting standard: sets the projection angle and how dimension text reads.</summary>
public enum DraftingStandard
{
    /// <summary>First-angle projection, dimension text aligned with the dimension line.</summary>
    Iso,

    /// <summary>Third-angle projection, dimension text horizontal (unidirectional).</summary>
    Ansi,
}

/// <summary>A free text note on a sheet.</summary>
public sealed class Note
{
    public string? Layer { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = string.Empty;
    public double Height { get; set; } = 3.5;
}

/// <summary>A drawing view of the model.</summary>
public sealed class DrawingView
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ViewKind Kind { get; set; }

    /// <summary>Centre of the view on the sheet (mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>View scale; null uses the sheet's.</summary>
    public double? Scale { get; set; }

    public bool ShowHidden { get; set; }

    public bool ShowTangent { get; set; } = true;

    /// <summary>For section and detail views: the view they're taken from.</summary>
    public string? ParentId { get; set; }

    /// <summary>Section line (two points) or detail circle centre, in the parent view's model-space coordinates.</summary>
    public double Ax { get; set; }

    public double Ay { get; set; }

    public double Bx { get; set; }

    public double By { get; set; }

    /// <summary>Detail circle radius (model mm).</summary>
    public double Radius { get; set; }

    /// <summary>Section or detail label ("A", "B", ...).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>For projected views: the side of the parent.</summary>
    public ViewSide Side { get; set; }

    /// <summary>Crop rectangle in view coordinates (min x, min y, max x, max y); null shows the whole view.</summary>
    public double[]? Crop { get; set; }

    /// <summary>Break: the band from Break[0] to Break[1] (view coordinates) is left out and the rest closed up.</summary>
    public double[]? Break { get; set; }

    /// <summary>The break band runs across Y (a vertical part shortened) instead of across X.</summary>
    public bool BreakAlongY { get; set; }
}

/// <summary>A bill of materials table on a sheet (assembly drawings).</summary>
public sealed class BomTable
{
    /// <summary>Top-left corner on the sheet (mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>A numbered balloon pointing at a component in a view.</summary>
public sealed class Balloon
{
    public string ViewId { get; set; } = string.Empty;

    public string ComponentId { get; set; } = string.Empty;

    /// <summary>Balloon centre on the sheet (mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>One sheet of a drawing.</summary>
public sealed class Sheet
{
    public string Name { get; set; } = "Sheet1";

    public SheetSize Size { get; set; } = SheetSize.A3;

    public bool Landscape { get; set; } = true;

    /// <summary>Default view scale (sheet mm per model mm).</summary>
    public double Scale { get; set; } = 1;

    public List<DrawingView> Views { get; set; } = [];

    public List<DrawingDimension> Dimensions { get; set; } = [];

    public List<Note> Notes { get; set; } = [];

    public BomTable? Bom { get; set; }

    public List<Balloon> Balloons { get; set; } = [];

    public List<GeometricTolerance> Tolerances { get; set; } = [];

    public List<DatumFeature> Datums { get; set; } = [];

    public List<SurfaceFinish> Finishes { get; set; } = [];

    public List<HoleTable> HoleTables { get; set; } = [];

    public RevisionTable? Revisions { get; set; }

    public (double Width, double Height) Dimensions2D
    {
        get
        {
            var (w, h) = Size switch
            {
                SheetSize.A4 => (297.0, 210.0),
                SheetSize.A3 => (420.0, 297.0),
                SheetSize.A2 => (594.0, 420.0),
                SheetSize.A1 => (841.0, 594.0),
                SheetSize.A0 => (1189.0, 841.0),
                SheetSize.Letter => (279.4, 215.9),
                _ => (431.8, 279.4),
            };
            return Landscape ? (w, h) : (h, w);
        }
    }
}

/// <summary>
/// A drawing of a part: sheets with views projected from the model, plus
/// dimensions, notes and a title block. Views are regenerated from the model
/// whenever it changes, so the drawing stays associative.
/// </summary>
public sealed class DrawingDocument
{
    public const int FormatVersion = 1;
    public const string FileExtension = "chdrw";

    private int _nextId = 1;

    /// <summary>The part file shown, relative to the drawing's folder when saved.</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>Part configuration to show; null uses the part's active one.</summary>
    public string? Configuration { get; set; }

    public List<Sheet> Sheets { get; } = [new Sheet()];

    /// <summary>Title block fields: Title, Number, Author, Date, Revision, Company, Material.</summary>
    public Dictionary<string, string> Properties { get; } = [];

    /// <summary>Third-angle (ANSI) or first-angle (ISO) projection for standard views.</summary>
    public bool FirstAngle { get; set; }

    public DraftingStandard Standard { get; set; } = DraftingStandard.Iso;

    public List<DrawingLayer> Layers { get; } = [];

    /// <summary>Sets the standard and the projection angle that goes with it.</summary>
    public void ApplyStandard(DraftingStandard standard)
    {
        Standard = standard;
        FirstAngle = standard == DraftingStandard.Iso;
    }

    /// <summary>False when the named layer exists and is hidden.</summary>
    public bool IsLayerVisible(string? layer) => layer is null || Layers.Find(l => l.Name == layer) is not { Visible: false };

    /// <summary>Where the drawing file lives; the model path is resolved relative to it.</summary>
    [JsonIgnore]
    public string? Directory { get; set; }

    /// <summary>Loads the model. Defaults to reading the part file from disk.</summary>
    [JsonIgnore]
    public Func<string, PartDocument> ModelLoader { get; set; }

    private PartDocument? _model;

    public DrawingDocument()
    {
        ModelLoader = path => PartDocument.Load(File.ReadAllText(
            Path.IsPathRooted(path) || Directory is null ? path : Path.GetFullPath(Path.Combine(Directory, path))));
    }

    /// <summary>True when the drawing shows an assembly (.chasm).</summary>
    public bool IsAssembly => ModelPath.EndsWith("." + AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase) || _assembly is not null;

    private AssemblyDocument? _assembly;
    private (string Key, IReadOnlyList<Body> Bodies)? _assemblyBodies;

    /// <summary>The assembly shown, for assembly drawings.</summary>
    public AssemblyDocument Assembly
    {
        get
        {
            if (_assembly is null)
            {
                var path = Path.IsPathRooted(ModelPath) || Directory is null ? ModelPath : Path.GetFullPath(Path.Combine(Directory, ModelPath));
                _assembly = AssemblyDocument.Load(File.ReadAllText(path), Path.GetDirectoryName(path));
                _assembly.Solve();
            }

            return _assembly;
        }
    }

    /// <summary>Uses an in-memory assembly.</summary>
    public void SetAssembly(AssemblyDocument assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _assembly = assembly;
        _assemblyBodies = null;
    }

    /// <summary>
    /// The solids the views show: the part's bodies, or every visible
    /// component's bodies placed in the assembly (named after the component).
    /// </summary>
    public IReadOnlyList<Body> Bodies
    {
        get
        {
            if (!IsAssembly)
            {
                return Model.Bodies;
            }

            var asm = Assembly;
            var visible = asm.Components.Where(c => !c.Suppressed && !c.Hidden).ToList();
            var key = string.Join(';', visible.Select(c => $"{c.Id}:{c.Transform.GetHashCode()}:{asm.PartOf(c).State.GetHashCode()}")) + "|" + asm.FeaturesFingerprint();
            if (_assemblyBodies is { } cached && cached.Key == key)
            {
                return cached.Bodies;
            }

            var bodies = new List<Body>();
            foreach (var c in visible)
            {
                foreach (var (body, placed) in asm.PlacedBodies(c))
                {
                    bodies.Add(new Body(placed, body.FaceNames, c.Id));
                }
            }

            _assemblyBodies = (key, bodies);
            return bodies;
        }
    }

    /// <summary>Units for dimension values: the part's units (assemblies use millimetres).</summary>
    public LengthUnit LengthUnit => IsAssembly ? LengthUnit.Millimeter : Model.Units;

    /// <summary>The material shown in the title block.</summary>
    public string MaterialName => IsAssembly ? "See BOM" : Model.Material.Name;

    /// <summary>The rebuilt model (part drawings).</summary>
    public PartDocument Model
    {
        get
        {
            if (_model is null)
            {
                _model = ModelLoader(ModelPath);
                if (Configuration is { } c && _model.Configurations.Exists(x => x.Name == c))
                {
                    _model.ActiveConfiguration = c;
                }

                _model.Rebuild();
            }

            return _model;
        }
    }

    /// <summary>Uses an in-memory part (and forgets the cached one).</summary>
    public void SetModel(PartDocument part)
    {
        ArgumentNullException.ThrowIfNull(part);
        _model = part;
        _model.Rebuild();
    }

    /// <summary>Reloads the model from its file on next use.</summary>
    public void ReloadModel() => _model = null;

    public string NextId(string prefix) => $"{prefix}{_nextId++}";

    /// <summary>Adds Front, Top and Right views (third angle) or Front, Top-below and Left (first angle), laid out on the sheet.</summary>
    public IReadOnlyList<DrawingView> AddStandardViews(Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var (w, h) = sheet.Dimensions2D;
        var box = Bodies.Aggregate(Box3.Empty, (b, x) => b.Union(x.Shape.Bounds));
        var size = box.IsEmpty ? new Vec3(50, 50, 50) : box.Size;

        // Pick a standard scale that fits three views with margins.
        var needW = (size.X + size.Z) * 1.6;
        var needH = (size.Y + size.Z) * 1.6;
        sheet.Scale = Scales.Best(Math.Min((w - 60) / needW, (h - 80) / needH));
        var s = sheet.Scale;
        var gap = 20 + (0.3 * s * Math.Max(size.X, Math.Max(size.Y, size.Z)));
        var frontX = 30 + (s * size.X / 2) + 20;
        var frontY = 50 + (s * size.Y / 2) + (FirstAngle ? (s * size.Z) + gap : 0) + 10;
        var top = FirstAngle ? frontY - (s * (size.Y + size.Z) / 2) - gap : frontY + (s * (size.Y + size.Z) / 2) + gap;
        var side = frontX + (s * (size.X + size.Z) / 2) + gap;

        var views = new List<DrawingView>
        {
            Add(sheet, ViewKind.Front, frontX, frontY),
            Add(sheet, ViewKind.Top, frontX, top),
            Add(sheet, FirstAngle ? ViewKind.Left : ViewKind.Right, side, frontY),
            Add(sheet, ViewKind.Isometric, w - 30 - (s * 0.6 * size.Length / 2.5), h - 30 - (s * 0.6 * size.Length / 2.5)),
        };
        views[^1].Scale = Scales.Best(s * 0.6);
        return views;
    }

    public DrawingView Add(Sheet sheet, ViewKind kind, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var view = new DrawingView
        {
            Id = NextId("v"),
            Name = $"Drawing View{sheet.Views.Count + 1}",
            Kind = kind,
            X = x,
            Y = y,
        };
        sheet.Views.Add(view);
        return view;
    }

    /// <summary>A section view through the line from (ax, ay) to (bx, by) in the parent view's model coordinates.</summary>
    public DrawingView AddSection(Sheet sheet, DrawingView parent, Point2 a, Point2 b, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(parent);
        var label = ((char)('A' + sheet.Views.Count(v => v.Kind is ViewKind.Section or ViewKind.Detail))).ToString();
        var view = Add(sheet, ViewKind.Section, x, y);
        view.ParentId = parent.Id;
        view.Name = $"Section {label}-{label}";
        view.Label = label;
        (view.Ax, view.Ay, view.Bx, view.By) = (a.X, a.Y, b.X, b.Y);
        return view;
    }

    /// <summary>A view projected from <paramref name="parent"/> to one side, aligned with it.</summary>
    public DrawingView AddProjected(Sheet sheet, DrawingView parent, ViewSide side, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(parent);
        var view = Add(sheet, ViewKind.Projected, x, y);
        view.ParentId = parent.Id;
        view.Side = side;
        view.Scale = parent.Scale;
        return view;
    }

    /// <summary>An auxiliary view looking square to the edge from <paramref name="a"/> to <paramref name="b"/> (parent view coordinates).</summary>
    public DrawingView AddAuxiliary(Sheet sheet, DrawingView parent, Point2 a, Point2 b, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(parent);
        var label = ((char)('A' + sheet.Views.Count(v => v.Kind is ViewKind.Section or ViewKind.Detail or ViewKind.Auxiliary))).ToString();
        var view = Add(sheet, ViewKind.Auxiliary, x, y);
        view.ParentId = parent.Id;
        view.Name = $"View {label}";
        view.Label = label;
        view.Scale = parent.Scale;
        (view.Ax, view.Ay, view.Bx, view.By) = (a.X, a.Y, b.X, b.Y);
        return view;
    }

    /// <summary>A detail view of the circle at <paramref name="center"/> (parent model coordinates), magnified.</summary>
    public DrawingView AddDetail(Sheet sheet, DrawingView parent, Point2 center, double radius, double magnification, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(parent);
        var label = ((char)('A' + sheet.Views.Count(v => v.Kind is ViewKind.Section or ViewKind.Detail))).ToString();
        var view = Add(sheet, ViewKind.Detail, x, y);
        view.ParentId = parent.Id;
        view.Name = $"Detail {label}";
        view.Label = label;
        (view.Ax, view.Ay, view.Radius) = (center.X, center.Y, radius);
        view.Scale = (parent.Scale ?? sheet.Scale) * magnification;
        return view;
    }

    /// <summary>
    /// Adds one balloon per distinct part visible in a view, placed around the
    /// view's outline in the direction of each component.
    /// </summary>
    public IReadOnlyList<Balloon> AutoBalloon(Sheet sheet, DrawingView view, DrawingRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(renderer);
        var vg = renderer.Geometry(sheet, view);
        var (min, max) = vg.SheetBounds;
        var center = (min + max) * 0.5;
        var radius = (Point2.Distance(min, max) / 2) + 18;
        var added = new List<Balloon>();
        var seen = new HashSet<(string, string?)>();
        foreach (var c in Assembly.Components.Where(c => !c.Suppressed && !c.Hidden))
        {
            if (!seen.Add((c.Path, c.Configuration)))
            {
                continue;
            }

            var b = new Balloon { ViewId = view.Id, ComponentId = c.Id };
            var anchor = renderer.BalloonAnchor(sheet, b);
            var dir = anchor - center;
            dir = dir.Length < 1e-6 ? new Point2(1, 1).Normalized() : dir.Normalized();

            // Keep inside the frame, and spread balloons that would land on top of each other.
            var (sw, sh) = sheet.Dimensions2D;
            var at = center + (dir * radius);
            at = new Point2(Math.Clamp(at.X, 18, sw - 18), Math.Clamp(at.Y, 18, sh - 18));
            while (sheet.Balloons.Concat(added).Any(o => Point2.Distance(new Point2(o.X, o.Y), at) < 11))
            {
                at += dir.Perp() * 11;
            }

            (b.X, b.Y) = (at.X, at.Y);
            added.Add(b);
        }

        sheet.Balloons.AddRange(added);
        return added;
    }

    public DrawingDimension AddDimension(Sheet sheet, DrawingView view, DimensionKind kind, DimensionAnchor a, DimensionAnchor? b = null, double offset = 10)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(view);
        var d = new DrawingDimension { Id = NextId("d"), ViewId = view.Id, Kind = kind, A = a, B = b, Offset = offset };
        sheet.Dimensions.Add(d);
        return d;
    }

    // ---------------------------------------------------------- persistence

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class DrawingDto
    {
        public int Version { get; set; }
        public string Format { get; set; } = "chamfer-drawing";
        public int NextId { get; set; }
        public string ModelPath { get; set; } = string.Empty;
        public string? Configuration { get; set; }
        public bool FirstAngle { get; set; }
        public DraftingStandard Standard { get; set; }
        public List<DrawingLayer>? Layers { get; set; }
        public Dictionary<string, string> Properties { get; set; } = [];
        public List<Sheet> Sheets { get; set; } = [];
    }

    public string Save() => JsonSerializer.Serialize(new DrawingDto
    {
        Version = FormatVersion,
        NextId = _nextId,
        ModelPath = ModelPath,
        Configuration = Configuration,
        FirstAngle = FirstAngle,
        Standard = Standard,
        Layers = Layers.Count > 0 ? Layers : null,
        Properties = Properties,
        Sheets = Sheets,
    }, Options);

    public static DrawingDocument Load(string json, string? directory = null)
    {
        var dto = JsonSerializer.Deserialize<DrawingDto>(json, Options) ?? throw new JsonException("Empty drawing document.");
        if (dto.Version > FormatVersion)
        {
            throw new NotSupportedException($"This drawing was saved by a newer version of Chamfer (format {dto.Version}).");
        }

        var doc = new DrawingDocument
        {
            ModelPath = dto.ModelPath,
            Configuration = dto.Configuration,
            FirstAngle = dto.FirstAngle,
            Standard = dto.Standard,
            Directory = directory,
            _nextId = dto.NextId,
        };
        foreach (var (k, v) in dto.Properties)
        {
            doc.Properties[k] = v;
        }

        doc.Layers.AddRange(dto.Layers ?? []);
        doc.Sheets.Clear();
        doc.Sheets.AddRange(dto.Sheets.Count > 0 ? dto.Sheets : [new Sheet()]);
        return doc;
    }
}

/// <summary>Preferred drawing scales.</summary>
public static class Scales
{
    private static readonly double[] Standard = [0.01, 0.02, 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5, 10, 20, 50];

    /// <summary>The largest standard scale not above <paramref name="fit"/>.</summary>
    public static double Best(double fit) => Standard.LastOrDefault(s => s <= fit + 1e-12, Standard[0]);

    public static string Format(double scale) => scale >= 1
        ? $"{scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}:1"
        : $"1:{(1 / scale).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
}
