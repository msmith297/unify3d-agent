using System.Collections.Generic;
using System.Linq;
using SwAgent.Watch;
using Xunit;

namespace SwAgent.Tests
{
    public class WatchDiffTests
    {
        internal static FeatureState F(int id, string name, string kind, string summary = null, params (string, string)[] p) => new FeatureState
        {
            Id = id,
            Name = name,
            Type = kind,
            Kind = kind,
            Summary = summary,
            Params = p.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList(),
        };

        [Fact]
        public void AddEditDeleteRename()
        {
            var before = new List<FeatureState>
            {
                F(1, "Boss-Extrude1", "Boss-Extrude", "end blind 10 mm", ("end", "blind 10 mm"), ("D1@Boss-Extrude1", "10 mm")),
                F(2, "Fillet1", "Fillet"),
                F(3, "Sketch2", "Sketch"),
            };
            var after = new List<FeatureState>
            {
                F(1, "Boss-Extrude1", "Boss-Extrude", "end blind 15 mm", ("end", "blind 15 mm"), ("D1@Boss-Extrude1", "15 mm")),
                F(3, "Hole sketch", "Sketch"),
                F(4, "Fillet3", "Fillet", "constant radius 2 mm on 4 edges"),
            };
            var changes = WatchDiff.Diff(before, after);
            Assert.Equal(new[] { "edit", "rename", "add", "delete" }, changes.Select(c => c.Kind));
            Assert.Equal("Edited Boss-Extrude1 (Boss-Extrude): end blind 10 mm -> blind 15 mm", WatchDiff.Describe(changes[0]));
            Assert.Equal("Renamed Sketch2 -> Hole sketch", WatchDiff.Describe(changes[1]));
            Assert.Equal("Added Fillet3 (Fillet): constant radius 2 mm on 4 edges", WatchDiff.Describe(changes[2]));
            Assert.Equal("Deleted Fillet1 (Fillet)", WatchDiff.Describe(changes[3]));
        }

        [Fact]
        public void DimensionChange_IsReportedWhenNoNamedParameterShowsIt()
        {
            var a = F(1, "Sketch1", "Sketch", null, ("D1@Sketch1", "40 mm"));
            var b = F(1, "Sketch1", "Sketch", null, ("D1@Sketch1", "50 mm"));
            var c = WatchDiff.Diff(new[] { a }, new[] { b }).Single();
            Assert.Equal("Edited Sketch1 (Sketch): D1@Sketch1 40 mm -> 50 mm", WatchDiff.Describe(c));
        }

        [Fact]
        public void Suppression_IsItsOwnChange()
        {
            var a = F(1, "Fillet1", "Fillet", null, ("radius", "2 mm"));
            var b = F(1, "Fillet1", "Fillet");
            b.Suppressed = true;
            var c = WatchDiff.Diff(new[] { a }, new[] { b }).Single();
            Assert.Equal("suppress", c.Kind);
        }

        [Fact]
        public void Selection_Summaries()
        {
            Assert.Null(SelectionSummary.Summarize(new (string, string, string)[0]));
            Assert.Equal("face of Boss-Extrude1 (planar, area 100 mm²)", SelectionSummary.Summarize(new[] { ("Face", "Boss-Extrude1", "planar, area 100 mm²") }));
            var many = Enumerable.Repeat(("Edge", "Boss-Extrude1", "line"), 4).Concat(new[] { ("Face", "Cut-Extrude1", "planar") });
            Assert.Equal("4 edges of Boss-Extrude1, 1 face of Cut-Extrude1", SelectionSummary.Summarize(many));
            Assert.Equal("vertices", SelectionSummary.Plural("vertex", 3));
            Assert.Equal("bodies", SelectionSummary.Plural("body", 2));
        }
    }

    public class ActivityLogTests
    {
        private static FeatureChange Add(FeatureState f) => new FeatureChange { Kind = "add", After = f };

        private static FeatureChange Edit(FeatureState a, FeatureState b) => WatchDiff.Diff(new[] { a }, new[] { b }).Single();

        private static FeatureState Fillet(string r) =>
            WatchDiffTests.F(7, "Fillet3", "Fillet", $"constant radius {r} on 4 edges", ("radius", r));

        [Fact]
        public void Selection_IsFoldedIntoTheFeatureBuiltOnIt()
        {
            var log = new ActivityLog();
            log.AddSelection("2 edges of Boss-Extrude1", 1);
            log.AddSelection("4 edges of Boss-Extrude1", 2); // replaces the previous selection
            Assert.Single(log.Entries);
            log.AddChange(Add(Fillet("2 mm")), 5);
            var e = log.Entries.Single();
            Assert.Equal("add", e.Kind);
            Assert.Equal("Added Fillet3 (Fillet): constant radius 2 mm on 4 edges - with 4 edges of Boss-Extrude1 selected", e.Text);
        }

        [Fact]
        public void EmptySelection_IsIgnored()
        {
            var log = new ActivityLog();
            Assert.Null(log.AddSelection("", 1));
            Assert.Empty(log.Entries);
        }

        [Fact]
        public void EditRightAfterAdd_UpdatesTheAdd()
        {
            var log = new ActivityLog();
            log.AddChange(Add(Fillet("2 mm")), 1);
            log.AddChange(Edit(Fillet("2 mm"), Fillet("3 mm")), 10);
            Assert.Equal("Added Fillet3 (Fillet): constant radius 3 mm on 4 edges", log.Entries.Single().Text);
        }

        [Fact]
        public void RepeatedEdits_MergeAndCancel()
        {
            var a = WatchDiffTests.F(1, "Boss-Extrude1", "Boss-Extrude", null, ("end", "blind 10 mm"));
            var b = WatchDiffTests.F(1, "Boss-Extrude1", "Boss-Extrude", null, ("end", "blind 12 mm"));
            var c = WatchDiffTests.F(1, "Boss-Extrude1", "Boss-Extrude", null, ("end", "blind 15 mm"));
            var log = new ActivityLog();
            log.AddChange(Edit(a, b), 1);
            log.AddChange(Edit(b, c), 3);
            Assert.Equal("Edited Boss-Extrude1 (Boss-Extrude): end blind 10 mm -> blind 15 mm", log.Entries.Single().Text);
            log.AddChange(Edit(c, a), 6); // back where it started
            Assert.Empty(log.Entries);
        }

        [Fact]
        public void EditsFarApart_StaySeparate()
        {
            var a = WatchDiffTests.F(1, "Boss-Extrude1", "Boss-Extrude", null, ("end", "blind 10 mm"));
            var b = WatchDiffTests.F(1, "Boss-Extrude1", "Boss-Extrude", null, ("end", "blind 12 mm"));
            var c = WatchDiffTests.F(1, "Boss-Extrude1", "Boss-Extrude", null, ("end", "blind 15 mm"));
            var log = new ActivityLog();
            log.AddChange(Edit(a, b), 1);
            log.AddChange(Edit(b, c), 1 + ActivityLog.MergeSeconds + 1);
            Assert.Equal(2, log.Entries.Count);
        }

        [Fact]
        public void AddThenDelete_DisappearsWithItsEdits()
        {
            var log = new ActivityLog();
            log.AddChange(Add(WatchDiffTests.F(9, "Sketch1", "Sketch")), 1);
            log.AddChange(Add(Fillet("2 mm")), 2);
            log.AddChange(Edit(Fillet("2 mm"), Fillet("4 mm")), 100);
            log.AddChange(new FeatureChange { Kind = "delete", Before = Fillet("4 mm") }, 101);
            Assert.Equal("Added Sketch1 (Sketch)", log.Entries.Single().Text);
        }

        [Fact]
        public void DeletingAnExistingFeature_IsKept()
        {
            var log = new ActivityLog();
            log.AddChange(new FeatureChange { Kind = "delete", Before = Fillet("2 mm") }, 1);
            Assert.Equal("Deleted Fillet3 (Fillet)", log.Entries.Single().Text);
        }

        [Fact]
        public void RenameAfterAdd_RenamesTheAdd()
        {
            var log = new ActivityLog();
            log.AddChange(Add(Fillet("2 mm")), 1);
            var renamed = Fillet("2 mm");
            renamed.Name = "Edge round";
            log.AddChange(new FeatureChange { Kind = "rename", Before = Fillet("2 mm"), After = renamed }, 3);
            Assert.StartsWith("Added Edge round (Fillet)", log.Entries.Single().Text);
        }

        [Fact]
        public void AgentChanges_AreMarked()
        {
            var log = new ActivityLog();
            log.AddChange(Add(Fillet("2 mm")), 1, byAgent: true);
            Assert.EndsWith("(by the agent)", log.ToText());
        }

        [Fact]
        public void ToText_ElidesTheMiddle()
        {
            var log = new ActivityLog();
            for (var i = 0; i < 200; i++) log.AddChange(Add(WatchDiffTests.F(100 + i, $"Feature{i}", "Fillet", new string('x', 60))), i * 60);
            var text = log.ToText(3000);
            Assert.True(text.Length <= 3000, text.Length.ToString());
            Assert.Contains("1. [0:00] Added Feature0", text);
            Assert.Contains("steps omitted", text);
            Assert.Contains("200. [3:19:00] Added Feature199", text);
        }

        [Fact]
        public void Report_ListsFinalParametersOfTouchedFeatures()
        {
            var log = new ActivityLog();
            var fillet = Fillet("2 mm");
            fillet.Parents = "Boss-Extrude1";
            log.AddChange(Add(fillet), 4);
            var untouched = WatchDiffTests.F(1, "Boss-Extrude1", "Boss-Extrude", null, ("end", "blind 10 mm"));
            var text = WatchReport.Compose("Bracket.SLDPRT", "part", 75, log, new[] { untouched, fillet }, 1);
            Assert.Contains("Recorded with \"Watch me\" in Bracket.SLDPRT (part) over 1 min 15 s: 1 step, feature tree 1 -> 2 features.", text);
            Assert.Contains("1. [0:04] Added Fillet3", text);
            Assert.Contains("- Fillet3 (Fillet) <- Boss-Extrude1: radius 2 mm", text);
            Assert.DoesNotContain("blind 10 mm", text);
        }

        [Fact]
        public void Report_WhenNothingHappened()
        {
            var text = WatchReport.Compose("Part1", "part", 5, new ActivityLog(), new FeatureState[0], 0);
            Assert.Contains("Nothing changed", text);
        }
    }
}
