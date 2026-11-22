using Chamfer.Geometry;

namespace Chamfer.Document.Tests;

public class UnitsTests
{
    [Fact]
    public void FormatsAndSplitsSuffixes()
    {
        Assert.Equal("1", Units.Format(25.4, LengthUnit.Inch));
        Assert.Equal("0.5 in", Units.Format(12.7, LengthUnit.Inch, withSuffix: true));
        Assert.Equal("2.5", Units.Format(25, LengthUnit.Centimeter));
        Assert.Equal("0.125", Units.Format(125, LengthUnit.Meter));
        Assert.Equal("IPS", Units.SystemName(LengthUnit.Inch));
        Assert.Equal(("1", 25.4), Units.SplitSuffix("1in"));
        Assert.Equal(("2", 25.4), Units.SplitSuffix("2\""));
        Assert.Equal(("25", 1.0), Units.SplitSuffix("25 mm"));
        Assert.Equal(("(1+2)", 1000.0), Units.SplitSuffix("(1+2)m"));
        Assert.Equal(("Width/2", null), Units.SplitSuffix("Width/2"));
        Assert.Equal(("cm", null), Units.SplitSuffix("cm"));

        var doc = new PartDocument { Units = LengthUnit.Inch };
        Assert.Equal(LengthUnit.Inch, PartDocument.Load(doc.Save()).Units);
        Assert.Equal(LengthUnit.Millimeter, PartDocument.Load(new PartDocument().Save()).Units);
    }
}
