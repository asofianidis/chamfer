using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Sketching.Tests;

public class BlockTests
{
    [Fact]
    public void BlocksGroupCopyAndSave()
    {
        var s = new Sketch();
        var sides = s.AddCornerRectangle(new Vec2(0, 0), new Vec2(20, 10));
        var relations = s.Constraints.Count;
        var block = SketchBlocks.Make(s, "Pad", sides);
        Assert.Equal(4, block.EntityIds.Count);
        Assert.Same(block, SketchBlocks.BlockOf(s, sides[2].Start)); // a corner point belongs through its line

        var copy = SketchBlocks.Copy(s, block, new Vec2(50, 0));
        Assert.Equal(8, s.Entities.OfType<SketchLine>().Count());
        Assert.Equal(2 * relations, s.Constraints.Count); // the copy's own relations came along
        var copied = SketchBlocks.Entities(s, copy).OfType<SketchLine>().ToList();
        Assert.Equal(4, copied.Count);
        Assert.Contains(copied, l => l.Start.Position == new Vec2(50, 0) || l.End.Position == new Vec2(50, 0));
        Assert.True(s.Solve().Converged);

        // Saved with the sketch.
        var back = SketchSerializer.Deserialize(SketchSerializer.Serialize(s));
        Assert.Equal(2, back.Blocks.Count);
        Assert.All(back.Blocks, b => Assert.Equal("Pad", b.Name));

        // Deleting a block's geometry shrinks it; an empty block goes away.
        foreach (var l in copied)
        {
            s.Remove(l);
        }

        Assert.Single(s.Blocks);
        SketchBlocks.Explode(s, block);
        Assert.Empty(s.Blocks);
        Assert.Equal(4, s.Entities.OfType<SketchLine>().Count());
    }
}
