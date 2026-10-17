using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Millwork.Support;
using Xunit;

namespace RevitMCPCommandSet.Services.Tests
{
    public class MillworkLayoutTests
    {
        private static MillworkDesign Base(double w = 800, double h = 900, double d = 560) => new MillworkDesign
        {
            Kind = "base", Width = w, Height = h, Depth = d, Level = "Térreo", Point = new PointMm { X = 0, Y = 0 }
        };

        private static List<MillworkPart> Build(MillworkDesign d, out ResolvedDesign r)
        {
            r = MillworkLayout.Resolve(d);
            return MillworkLayout.Build(r, new List<string>());
        }

        private static bool Overlap(MillworkPart a, MillworkPart b)
        {
            const double e = 0.01;
            return a.Box[0] < b.Box[3] - e && b.Box[0] < a.Box[3] - e &&
                   a.Box[1] < b.Box[4] - e && b.Box[1] < a.Box[4] - e &&
                   a.Box[2] < b.Box[5] - e && b.Box[2] < a.Box[5] - e;
        }

        private static void AssertNoOverlaps(List<MillworkPart> parts)
        {
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                    Assert.False(Overlap(parts[i], parts[j]), $"{parts[i].Label} overlaps {parts[j].Label}");
        }

        [Fact]
        public void Base_defaults_rails_toe_kick_and_back()
        {
            var parts = Build(Base(), out var r);
            Assert.Equal("rails", r.TopPanel);
            Assert.Equal(100, r.ToeKickHeight);
            Assert.Contains(parts, p => p.Role == "rail_front");
            Assert.Contains(parts, p => p.Role == "back");
            Assert.Contains(parts, p => p.Role == "toe_kick_front");
            var side = parts.Single(p => p.Role == "side_left");
            Assert.Equal(new[] { 800.0, 560, 18 }, side.PanelDims());
            AssertNoOverlaps(parts);
        }

        [Fact]
        public void Doors_drawers_shelves_dividers_do_not_collide()
        {
            var d = Base(1200);
            d.Doors = 2; d.Drawers = 2; d.Shelves = 1; d.Dividers = 1;
            var parts = Build(d, out var r);
            Assert.Equal(2, parts.Count(p => p.Piece == "Porta"));
            Assert.Equal(2, parts.Count(p => p.Piece == "Frente de gaveta"));
            Assert.Equal(2, parts.Count(p => p.Piece == "Prateleira"));
            Assert.Single(parts, p => p.Piece == "Divisória");
            Assert.Equal(new List<double> { 180, 180 }, r.DrawerHeightsUsed);
            Assert.Equal("hinge:left", parts.Single(p => p.Role == "door_1").Note);
            Assert.Equal("hinge:right", parts.Single(p => p.Role == "door_2").Note);
            AssertNoOverlaps(parts);
        }

        [Fact]
        public void Inset_wall_module_with_lift_door()
        {
            var d = new MillworkDesign
            {
                Kind = "wall", Width = 900, Height = 700, Depth = 350, Level = "1", Point = new PointMm(),
                Doors = 1, DoorOpening = "lift_up", FrontStyle = "inset", Shelves = 2
            };
            var parts = Build(d, out var r);
            Assert.Equal(1500, r.Elevation);
            Assert.Equal("full", r.TopPanel);
            Assert.DoesNotContain(parts, p => p.Piece == "Rodapé");
            var door = parts.Single(p => p.Role == "door_1");
            Assert.Equal("hinge:top", door.Note);
            Assert.True(door.Box[0] >= 18 && door.Box[3] <= 900 - 18);
            AssertNoOverlaps(parts);
        }

        [Fact]
        public void Drawer_unit_fills_height_and_rejects_shelves()
        {
            var d = Base(600);
            d.Drawers = 4;
            var parts = Build(d, out var r);
            Assert.Equal(4, r.DrawerHeightsUsed.Count);
            AssertNoOverlaps(parts);
            d.Shelves = 1;
            Assert.Throws<ToolException>(() => Build(d, out _));
        }

        [Fact]
        public void Configure_merges_changes_over_baseline()
        {
            var r0 = MillworkLayout.Resolve(Base());
            var r1 = MillworkLayout.Resolve(new MillworkDesign { Doors = 1, HingeSide = "right", Width = 450 }, r0);
            Assert.Equal(450, r1.Width);
            Assert.Equal(900, r1.Height);
            Assert.Equal("right", r1.HingeSide);
            Assert.Equal("rails", r1.TopPanel);
            var r2 = MillworkLayout.Resolve(new MillworkDesign { Kind = "wall" }, r1);
            Assert.Equal(0, r2.ToeKickHeight);
            Assert.Equal("full", r2.TopPanel);
        }

        [Fact]
        public void Wall_module_with_toe_kick_is_refused()
        {
            var d = new MillworkDesign { Kind = "wall", Width = 600, Height = 700, Depth = 350, Level = "1", Point = new PointMm(), ToeKickHeight = 100 };
            Assert.Throws<ToolException>(() => Build(d, out _));
        }
    }
}
