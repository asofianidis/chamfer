using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Sketching;

/// <summary>A named group of sketch entities that select, move and copy together.</summary>
public sealed class SketchBlock
{
    public string Name { get; set; } = "Block";

    /// <summary>Ids of the block's top-level entities.</summary>
    public List<int> EntityIds { get; set; } = [];
}

public static class SketchBlocks
{
    /// <summary>Groups top-level entities into a new block (taking them out of any block they were in).</summary>
    public static SketchBlock Make(Sketch sketch, string name, IEnumerable<SketchEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);
        var ids = entities.Select(e => e.Root).Where(e => !ReferenceEquals(e, sketch.Origin)).Select(e => e.Id).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Select the geometry for the block.", nameof(entities));
        }

        foreach (var b in sketch.Blocks)
        {
            b.EntityIds.RemoveAll(ids.Contains);
        }

        sketch.Blocks.RemoveAll(b => b.EntityIds.Count == 0);
        var block = new SketchBlock { Name = name, EntityIds = ids };
        sketch.Blocks.Add(block);
        return block;
    }

    /// <summary>The block an entity belongs to, if any.</summary>
    public static SketchBlock? BlockOf(Sketch sketch, SketchEntity entity)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entity);
        return sketch.Blocks.Find(b => b.EntityIds.Contains(entity.Root.Id));
    }

    /// <summary>The block's entities (that still exist).</summary>
    public static IReadOnlyList<SketchEntity> Entities(Sketch sketch, SketchBlock block)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(block);
        return [.. block.EntityIds.Select(sketch.FindEntity).OfType<SketchEntity>()];
    }

    /// <summary>Dissolves a block: its entities stay, ungrouped.</summary>
    public static void Explode(Sketch sketch, SketchBlock block)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        sketch.Blocks.Remove(block);
    }

    /// <summary>
    /// Adds a copy of the block, moved by <paramref name="offset"/>, with the relations among its own
    /// geometry; returns the new block (named like the original).
    /// </summary>
    public static SketchBlock Copy(Sketch sketch, SketchBlock block, Vec2 offset)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(block);

        // A scratch copy of the sketch through the file format gives fresh, unshared objects.
        var scratch = SketchSerializer.Deserialize(SketchSerializer.Serialize(sketch));
        var keep = block.EntityIds.Select(scratch.FindEntity).OfType<SketchEntity>().ToList();
        var keepSet = new HashSet<SketchEntity>(keep.SelectMany(e => e.Points.Append(e)));
        var relations = scratch.Constraints.Where(c => c.References.All(keepSet.Contains)).ToList();

        // Detach from the scratch sketch: fresh ids in the target, moved by the offset.
        foreach (var e in keep)
        {
            foreach (var p in e.Points.Append(e).OfType<SketchPoint>())
            {
                p.Position += offset;
            }

            e.Id = 0;
            foreach (var p in e.Points)
            {
                p.Id = 0;
            }
        }

        var added = keep.Select(sketch.Add).ToList();
        foreach (var c in relations.Where(c => c.Kind != Constraints.ConstraintKind.Fix))
        {
            c.Id = 0;
            sketch.AddConstraint(c);
        }

        var copy = new SketchBlock { Name = block.Name, EntityIds = [.. added.Select(e => e.Id)] };
        sketch.Blocks.Add(copy);
        return copy;
    }
}
