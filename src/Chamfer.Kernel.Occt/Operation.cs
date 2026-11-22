namespace Chamfer.Kernel.Occt;

/// <summary>
/// The result of a modelling operation together with its history, which maps
/// sub-shapes of the inputs to the sub-shapes of the result they became. The
/// history is what persistent naming is built on.
/// </summary>
public sealed unsafe class Operation : IDisposable
{
    private readonly OpHandle _handle;

    internal Operation(OpHandle handle, string what)
    {
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new KernelException($"{what}: {Native.LastError()}");
        }

        _handle = handle;
        Result = Shape.Wrap(Native.chf_op_shape(handle), what);
    }

    /// <summary>The result. Owned by the caller once taken; disposing the operation does not dispose it.</summary>
    public Shape Result { get; }

    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// Result sub-shapes of kind <paramref name="of"/> that input sub-shape
    /// (<paramref name="kind"/>, <paramref name="index"/>) was modified into,
    /// including itself when it passed through unchanged. Null when it was deleted.
    /// </summary>
    public int[]? Modified(Shape input, TopoKind kind, int index, TopoKind of)
    {
        ArgumentNullException.ThrowIfNull(input);
        var deleted = false;
        var list = Shape.ReadList((p, cap) =>
        {
            var n = Native.chf_op_history(_handle, input.Handle, (int)kind, index, 0, (int)of, p, cap);
            if (n == -1)
            {
                deleted = true;
                return 0;
            }

            return n;
        }, "history");
        return deleted ? null : list;
    }

    /// <summary>Result sub-shapes of kind <paramref name="of"/> generated from an input sub-shape (e.g. side faces from profile edges).</summary>
    public int[] Generated(Shape input, TopoKind kind, int index, TopoKind of)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Shape.ReadList((p, cap) => Native.chf_op_history(_handle, input.Handle, (int)kind, index, 1, (int)of, p, cap), "history");
    }

    /// <summary>Start cap faces of an extrude or revolve.</summary>
    public int[] StartCaps => Shape.ReadList((p, cap) => Native.chf_op_caps(_handle, 0, p, cap), "caps");

    /// <summary>End cap faces of an extrude or revolve.</summary>
    public int[] EndCaps => Shape.ReadList((p, cap) => Native.chf_op_caps(_handle, 1, p, cap), "caps");
}
