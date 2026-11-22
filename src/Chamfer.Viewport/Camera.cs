using Chamfer.Geometry;

namespace Chamfer.Viewport;

public enum StandardView
{
    Front,
    Back,
    Left,
    Right,
    Top,
    Bottom,
    Isometric,
    Trimetric,
    Dimetric,
}

/// <summary>
/// A view camera looking at <see cref="Target"/>. The orientation is an
/// orthonormal frame (right, up, back); the camera looks along -back. The
/// projection is orthographic by default with <see cref="ViewHeight"/> world
/// units visible vertically; perspective uses the same visible height at the
/// target.
/// </summary>
public sealed class Camera
{
    private const double FieldOfView = 30 * Math.PI / 180;

    public Vec3 Target { get; set; } = Vec3.Zero;
    public Vec3 Right { get; private set; } = Vec3.UnitX;
    public Vec3 Up { get; private set; } = Vec3.UnitY;
    public Vec3 Back { get; private set; } = Vec3.UnitZ;

    /// <summary>World units visible from the bottom to the top of the view, at the target.</summary>
    public double ViewHeight { get; set; } = 200;

    public bool Perspective { get; set; }

    /// <summary>Eye distance from the target in perspective mode.</summary>
    public double Distance => ViewHeight / 2 / Math.Tan(FieldOfView / 2);

    public Vec3 Eye => Target + (Back * Distance);

    public Camera Clone() => new()
    {
        Target = Target,
        Right = Right,
        Up = Up,
        Back = Back,
        ViewHeight = ViewHeight,
        Perspective = Perspective,
    };

    public void SetOrientation(Vec3 back, Vec3 up)
    {
        var b = back.Normalized();
        var r = Vec3.Cross(up, b).Normalized();
        if (r.LengthSquared < 0.5)
        {
            r = b.AnyPerpendicular();
        }

        Back = b;
        Right = r;
        Up = Vec3.Cross(b, r);
    }

    public void SetView(StandardView view)
    {
        switch (view)
        {
            case StandardView.Front: SetOrientation(Vec3.UnitZ, Vec3.UnitY); break;
            case StandardView.Back: SetOrientation(-Vec3.UnitZ, Vec3.UnitY); break;
            case StandardView.Left: SetOrientation(-Vec3.UnitX, Vec3.UnitY); break;
            case StandardView.Right: SetOrientation(Vec3.UnitX, Vec3.UnitY); break;
            case StandardView.Top: SetOrientation(Vec3.UnitY, -Vec3.UnitZ); break;
            case StandardView.Bottom: SetOrientation(-Vec3.UnitY, Vec3.UnitZ); break;
            case StandardView.Isometric: SetOrientation(new Vec3(1, 1, 1), Vec3.UnitY); break;
            case StandardView.Dimetric: SetOrientation(new Vec3(1, 0.53, 1), Vec3.UnitY); break;
            case StandardView.Trimetric: SetOrientation(new Vec3(1, 0.65, 1.6), Vec3.UnitY); break;
        }
    }

    /// <summary>Looks straight at a plane, keeping its X axis to the right.</summary>
    public void NormalTo(Plane3 plane)
    {
        Back = plane.Normal;
        Right = plane.XDirection;
        Up = plane.YDirection;
    }

    /// <summary>Pixels per world unit at the target for a viewport of the given height.</summary>
    public double Scale(int viewportHeight) => viewportHeight / ViewHeight;

    /// <summary>Projects to screen pixels; Z is distance along the view direction (larger = farther).</summary>
    public Vec3 Project(Vec3 p, int width, int height)
    {
        var d = p - Target;
        double x = Vec3.Dot(d, Right), y = Vec3.Dot(d, Up), z = Vec3.Dot(d, Back);
        var s = Scale(height);
        if (Perspective)
        {
            var depth = Distance - z;
            var k = Distance / Math.Max(depth, 1e-6);
            return new Vec3((width / 2.0) + (x * s * k), (height / 2.0) - (y * s * k), depth);
        }

        return new Vec3((width / 2.0) + (x * s), (height / 2.0) - (y * s), -z);
    }

    /// <summary>The ray through a screen pixel.</summary>
    public (Vec3 Origin, Vec3 Direction) Ray(double sx, double sy, int width, int height)
    {
        var s = Scale(height);
        var x = (sx - (width / 2.0)) / s;
        var y = ((height / 2.0) - sy) / s;
        if (Perspective)
        {
            var through = Target + (Right * x) + (Up * y);
            return (Eye, (through - Eye).Normalized());
        }

        var far = Math.Max(ViewHeight * 100, 1e5);
        return (Target + (Right * x) + (Up * y) + (Back * far), -Back);
    }

    /// <summary>Rotates the view by screen-space mouse motion (pixels) about a pivot.</summary>
    public void Orbit(double dx, double dy, Vec3? pivot = null, double radiansPerPixel = 0.008)
    {
        var center = pivot ?? Target;
        var yaw = Transform3.Rotation(Vec3.Zero, Up, -dx * radiansPerPixel);
        var pitch = Transform3.Rotation(Vec3.Zero, Right, -dy * radiansPerPixel);
        var r = pitch * yaw;
        Rotate(r, center);
    }

    /// <summary>Rotates the view about the view direction (roll).</summary>
    public void Roll(double radians)
    {
        var r = Transform3.Rotation(Vec3.Zero, Back, radians);
        Rotate(r, Target);
    }

    private void Rotate(Transform3 r, Vec3 center)
    {
        var back = r.ApplyVector(Back).Normalized();
        var up = r.ApplyVector(Up).Normalized();
        Target = center + r.ApplyVector(Target - center);
        SetOrientation(back, up);
    }

    /// <summary>Moves the view by screen pixels.</summary>
    public void Pan(double dx, double dy, int viewportHeight)
    {
        var s = Scale(viewportHeight);
        Target = Target - (Right * (dx / s)) + (Up * (dy / s));
    }

    /// <summary>Zooms by <paramref name="factor"/> (&lt;1 zooms in) keeping the given screen point fixed.</summary>
    public void Zoom(double factor, double sx, double sy, int width, int height)
    {
        var s = Scale(height);
        var x = (sx - (width / 2.0)) / s;
        var y = ((height / 2.0) - sy) / s;
        var anchor = Target + (Right * x) + (Up * y);
        ViewHeight = Math.Clamp(ViewHeight * factor, 1e-4, 1e7);
        var s2 = Scale(height);
        Target = anchor - (Right * ((sx - (width / 2.0)) / s2)) - (Up * (((height / 2.0) - sy) / s2));
    }

    /// <summary>Centres and scales the view on a box.</summary>
    public void Fit(Box3 box, int width, int height, double margin = 1.15)
    {
        if (box.IsEmpty)
        {
            return;
        }

        Target = box.Center;
        double maxX = 0, maxY = 0;
        foreach (var corner in Corners(box))
        {
            var d = corner - Target;
            maxX = Math.Max(maxX, Math.Abs(Vec3.Dot(d, Right)));
            maxY = Math.Max(maxY, Math.Abs(Vec3.Dot(d, Up)));
        }

        var aspect = height > 0 ? (double)width / height : 1;
        ViewHeight = Math.Max(Math.Max(2 * maxY, 2 * maxX / aspect) * margin, 1e-3);
    }

    private static IEnumerable<Vec3> Corners(Box3 b)
    {
        foreach (var x in new[] { b.Min.X, b.Max.X })
        {
            foreach (var y in new[] { b.Min.Y, b.Max.Y })
            {
                foreach (var z in new[] { b.Min.Z, b.Max.Z })
                {
                    yield return new Vec3(x, y, z);
                }
            }
        }
    }
}
