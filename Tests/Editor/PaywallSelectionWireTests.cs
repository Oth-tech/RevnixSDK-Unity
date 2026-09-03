// The designed-paywall render contract v2 (REV-262), walked through the shared
// wire fixture.
//
// Every renderer of a PaywallBlockDoc carries a byte-identical copy of
// revnix-app/tests/fixtures/paywall-selection-wire.json and asserts the same
// cases: which blocks draw with their `selectedStyle`, which `visibility`
// hides, and what the tags in each block's copy resolve to. The UGUI renderer
// cannot be exercised here (no scene, no play mode), so the rules it applies
// live in Runtime/Core — RevnixPaywallSelection — and that is what is asserted.
//
// Everything asserted here is in Runtime/Core, which is deliberately free of
// UnityEngine; the fixture is read straight from disk next to this file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class PaywallSelectionWireTests
    {
        private const string FixtureName = "paywall-selection-wire";

        private static string FixturePath([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile) ?? "", "Resources", FixtureName + ".json");

        private static Dictionary<string, object> LoadFixture()
        {
            string json = null;
            var path = FixturePath();
            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }
            else
            {
                // A package imported from its cache keeps the same folder
                // layout, but if the source path is gone the asset still is.
                var asset = UnityEngine.Resources.Load<UnityEngine.TextAsset>(FixtureName);
                if (asset != null) json = asset.text;
            }
            Assert.IsNotNull(json, "fixture not found at " + path);
            return RevnixJson.ParseObject(json);
        }

        private static List<BlockPackage> Packages(Dictionary<string, object> fixture)
        {
            var packages = new List<BlockPackage>();
            foreach (var item in RevnixJson.GetList(fixture, "packages"))
            {
                var map = item as Dictionary<string, object>;
                if (map == null) continue;
                packages.Add(new BlockPackage
                {
                    PackageId = RevnixJson.GetString(map, "packageId"),
                    Title = RevnixJson.GetString(map, "title"),
                    PriceLabel = RevnixJson.GetString(map, "priceLabel"),
                    Period = RevnixJson.GetString(map, "period"),
                    AmountMinor = RevnixJson.GetNullableLong(map, "amountMinor"),
                    Currency = RevnixJson.GetString(map, "currency"),
                });
            }
            return packages;
        }

        /// <summary>One resolution per block id. The fixture repeats no card,
        /// so ids are unique; the test says so rather than assuming it.</summary>
        private static Dictionary<string, BlockResolution> ById(List<BlockResolution> tree)
        {
            var byId = new Dictionary<string, BlockResolution>();
            foreach (var entry in tree)
            {
                Assert.IsFalse(byId.ContainsKey(entry.Block.Id), "duplicate block id " + entry.Block.Id);
                byId[entry.Block.Id] = entry;
            }
            return byId;
        }

        private static object StyleField(BlockStyle style, string key)
        {
            switch (key)
            {
                case "fill": return style?.Fill;
                case "textColor": return style?.TextColor;
                case "borderColor": return style?.BorderColor;
                case "borderWidth": return style?.BorderWidth;
                case "radius": return style?.Radius;
                case "opacity": return style?.Opacity;
                case "fontSize": return style?.FontSize;
                case "fontWeight": return style?.FontWeight;
                case "padding": return style?.Padding;
                case "gap": return style?.Gap;
                default:
                    Assert.Fail("the fixture asserts a style key this test does not map: " + key);
                    return null;
            }
        }

        private static void AssertStyleField(string caseName, string id, string key, object expected, object actual)
        {
            var where = caseName + " / " + id + "." + key;
            if (expected == null)
            {
                Assert.IsNull(actual, where);
                return;
            }
            if (expected is string s)
            {
                Assert.AreEqual(s, actual as string, where);
                return;
            }
            Assert.IsNotNull(actual, where);
            Assert.AreEqual(
                Convert.ToDouble(expected),
                Convert.ToDouble(actual),
                0.0001,
                where);
        }

        // ── The fixture itself ───────────────────────────────────────────────

        [Test]
        public void TheFixtureIsTheSharedOneAndParsesAsADesignedPaywall()
        {
            var fixture = LoadFixture();
            StringAssert.Contains("paywall-render-contract-v2", RevnixJson.GetString(fixture, "contract"));
            var doc = PaywallBlockDoc.Parse(RevnixJson.GetObject(fixture, "doc"));
            Assert.IsNotNull(doc);
            Assert.AreEqual(4, doc.Blocks.Count);
            Assert.AreEqual(4, RevnixJson.GetList(fixture, "cases").Count);
            Assert.AreEqual(2, Packages(fixture).Count);
        }

        // ── Section 1: the fields parse on every block kind ──────────────────

        [Test]
        public void SelectedStyleAndVisibilityParseOnBlocksThatAreNotCards()
        {
            var doc = PaywallBlockDoc.Parse(RevnixJson.ParseObject(@"{""blocks"":[
                {""id"":""t"",""type"":""text"",""text"":""x"",""visibility"":""selected"",
                 ""selectedStyle"":{""textColor"":""@accent""}},
                {""id"":""b"",""type"":""button"",""label"":""Go"",""visibility"":""unselected""},
                {""id"":""i"",""type"":""image"",""url"":""https://x/y.jpg"",""selectedStyle"":{""opacity"":50}},
                {""id"":""s"",""type"":""spacer"",""visibility"":""sometimes""}
            ]}"));
            Assert.IsNotNull(doc);
            Assert.AreEqual("selected", doc.Blocks[0].Visibility);
            Assert.AreEqual("@accent", doc.Blocks[0].SelectedStyle.TextColor);
            Assert.AreEqual("unselected", doc.Blocks[1].Visibility);
            Assert.AreEqual(50, doc.Blocks[2].SelectedStyle.Opacity);
            // A value this SDK does not know draws the block rather than
            // losing it.
            Assert.IsNull(doc.Blocks[3].Visibility);
        }

        // ── Sections 1 and 2: the wire cases ─────────────────────────────────

        [Test]
        public void EveryFixtureCaseResolvesAsTheContractSays()
        {
            var fixture = LoadFixture();
            var doc = PaywallBlockDoc.Parse(RevnixJson.GetObject(fixture, "doc"));
            var packages = Packages(fixture);
            var cases = RevnixJson.GetList(fixture, "cases");
            Assert.Greater(cases.Count, 0);

            foreach (var item in cases)
            {
                var c = (Dictionary<string, object>)item;
                var name = RevnixJson.GetString(c, "name", "?");
                var selected = RevnixPaywallSelection.ResolveSelectedPackageId(
                    packages,
                    RevnixJson.GetString(c, "selected"),
                    null,
                    RevnixJson.GetString(c, "highlight"));
                var byId = ById(RevnixPaywallSelection.ResolveTree(doc, packages, selected));

                var styles = RevnixJson.GetObject(c, "styles") ?? new Dictionary<string, object>();
                foreach (var pair in styles)
                {
                    Assert.IsTrue(byId.ContainsKey(pair.Key), name + ": styled block " + pair.Key + " is missing");
                    var expected = (Dictionary<string, object>)pair.Value;
                    var actual = byId[pair.Key].Block.Style;
                    foreach (var field in expected)
                    {
                        AssertStyleField(name, pair.Key, field.Key, field.Value, StyleField(actual, field.Key));
                    }
                }

                foreach (var id in RevnixJson.GetList(c, "visible"))
                {
                    var key = (string)id;
                    Assert.IsTrue(byId.ContainsKey(key), name + ": visible block " + key + " is missing");
                    Assert.IsTrue(byId[key].Visible, name + ": " + key + " must be visible");
                }

                foreach (var id in RevnixJson.GetList(c, "hidden"))
                {
                    var key = (string)id;
                    // A hidden block is listed (with its children pruned) or,
                    // for a pinned card past the offering, absent — both are
                    // "not drawn".
                    if (byId.ContainsKey(key))
                    {
                        Assert.IsFalse(byId[key].Visible, name + ": " + key + " must be hidden");
                    }
                }

                var texts = RevnixJson.GetObject(c, "texts") ?? new Dictionary<string, object>();
                foreach (var pair in texts)
                {
                    Assert.IsTrue(byId.ContainsKey(pair.Key), name + ": text block " + pair.Key + " is missing");
                    Assert.AreEqual((string)pair.Value, byId[pair.Key].Text, name + ": " + pair.Key);
                }
            }
        }

        // ── The selection rule ───────────────────────────────────────────────

        [Test]
        public void SelectedPackageIsHostThenTapThenHighlightThenFirst()
        {
            var packages = new List<BlockPackage>
            {
                new BlockPackage { PackageId = "monthly" },
                new BlockPackage { PackageId = "yearly" },
            };
            Assert.AreEqual("yearly", RevnixPaywallSelection.ResolveSelectedPackageId(packages, "yearly", "monthly", "monthly"));
            Assert.AreEqual("yearly", RevnixPaywallSelection.ResolveSelectedPackageId(packages, null, "yearly", "monthly"));
            Assert.AreEqual("yearly", RevnixPaywallSelection.ResolveSelectedPackageId(packages, null, null, "yearly"));
            Assert.AreEqual("monthly", RevnixPaywallSelection.ResolveSelectedPackageId(packages, null, null, null));
            // An id the offering does not carry does not count at any level.
            Assert.AreEqual("monthly", RevnixPaywallSelection.ResolveSelectedPackageId(packages, "weekly", "lifetime", "trial"));
            Assert.IsNull(RevnixPaywallSelection.ResolveSelectedPackageId(new List<BlockPackage>(), "yearly", null, null));
        }

        [Test]
        public void RootTagsResolveAgainstTheSelectedPackageAndCardTagsAgainstTheirOwn()
        {
            var packages = new List<BlockPackage>
            {
                new BlockPackage { PackageId = "monthly", PriceLabel = "$9.99", Period = "monthly" },
                new BlockPackage { PackageId = "yearly", PriceLabel = "$59.99", Period = "annual" },
            };
            var root = new PaywallBlock { Kind = PaywallBlockKind.Text, Id = "r", Text = "{price}/{period_short}" };
            var resolved = RevnixPaywallSelection.Resolve(root, null, packages, "yearly");
            Assert.AreEqual("$59.99/yr", resolved.Text);
            Assert.AreEqual(BlockSelectionContext.None, resolved.Context);

            var inside = RevnixPaywallSelection.Resolve(root, packages[0], packages, "yearly");
            Assert.AreEqual("$9.99/mo", inside.Text);
            Assert.AreEqual(BlockSelectionContext.Unselected, inside.Context);
        }

        [Test]
        public void ARootBlockIsNeverHiddenAndNeverTakesSelectedStyle()
        {
            var block = new PaywallBlock
            {
                Kind = PaywallBlockKind.Text,
                Id = "r",
                Text = "x",
                Visibility = "selected",
                Style = new BlockStyle { Fill = "#111111" },
                SelectedStyle = new BlockStyle { Fill = "#222222" },
            };
            Assert.IsTrue(RevnixPaywallSelection.IsVisible(block, BlockSelectionContext.None));
            Assert.AreSame(block.Style, RevnixPaywallSelection.EffectiveStyle(block, BlockSelectionContext.None));
            Assert.AreEqual("#222222", RevnixPaywallSelection.EffectiveStyle(block, BlockSelectionContext.Selected).Fill);
            Assert.IsFalse(RevnixPaywallSelection.IsVisible(block, BlockSelectionContext.Unselected));
        }

        [Test]
        public void ARepeatedCardResolvesOncePerPackageWithItsOwnContext()
        {
            var packages = new List<BlockPackage>
            {
                new BlockPackage { PackageId = "monthly", Title = "Monthly" },
                new BlockPackage { PackageId = "yearly", Title = "Yearly" },
            };
            var doc = PaywallBlockDoc.Parse(RevnixJson.ParseObject(@"{""blocks"":[
                {""id"":""rep"",""type"":""card"",""layout"":""column"",""repeat"":""packages"",
                 ""style"":{""fill"":""#111111""},""selectedStyle"":{""fill"":""#222222""},
                 ""children"":[
                   {""id"":""t"",""type"":""text"",""text"":""{title}""},
                   {""id"":""on"",""type"":""text"",""text"":""on"",""visibility"":""selected""}
                 ]}
            ]}"));
            var tree = RevnixPaywallSelection.ResolveTree(doc, packages, "yearly");
            var ids = new List<string>();
            foreach (var entry in tree) ids.Add(entry.Block.Id + ":" + (entry.Visible ? "v" : "h"));
            CollectionAssert.AreEqual(
                new[] { "rep:v", "t:v", "on:h", "rep:v", "t:v", "on:v" }, ids);
            Assert.AreEqual("#111111", tree[0].Block.Style.Fill);
            Assert.AreEqual("Monthly", tree[1].Text);
            Assert.AreEqual("#222222", tree[3].Block.Style.Fill);
            Assert.AreEqual("Yearly", tree[4].Text);
        }

        [Test]
        public void APinnedCardPastTheOfferingIsDroppedWithItsChildren()
        {
            var packages = new List<BlockPackage> { new BlockPackage { PackageId = "monthly" } };
            var doc = PaywallBlockDoc.Parse(RevnixJson.ParseObject(@"{""blocks"":[
                {""id"":""c9"",""type"":""card"",""layout"":""column"",""packageIndex"":9,
                 ""children"":[{""id"":""inner"",""type"":""text"",""text"":""x""}]},
                {""id"":""ok"",""type"":""text"",""text"":""y""}
            ]}"));
            var byId = ById(RevnixPaywallSelection.ResolveTree(doc, packages, "monthly"));
            Assert.IsFalse(byId.ContainsKey("c9"));
            Assert.IsFalse(byId.ContainsKey("inner"));
            Assert.IsTrue(byId.ContainsKey("ok"));
        }

        [Test]
        public void EffectiveStyleDoesNotMutateTheDocument()
        {
            var doc = PaywallBlockDoc.Parse(RevnixJson.ParseObject(@"{""blocks"":[
                {""id"":""c0"",""type"":""card"",""layout"":""row"",""packageIndex"":0,
                 ""style"":{""fill"":""#111111""},""selectedStyle"":{""fill"":""#222222""},""children"":[]}
            ]}"));
            var packages = new List<BlockPackage> { new BlockPackage { PackageId = "monthly" } };
            var resolved = RevnixPaywallSelection.Resolve(doc.Blocks[0], null, packages, "monthly");
            Assert.AreEqual("#222222", resolved.Block.Style.Fill);
            Assert.AreEqual("#111111", doc.Blocks[0].Style.Fill);
            Assert.AreNotSame(doc.Blocks[0], resolved.Block);
            // Nothing merged: the document's own instance comes back.
            var plain = RevnixPaywallSelection.Resolve(doc.Blocks[0], null, packages, "yearly");
            Assert.AreSame(doc.Blocks[0], plain.Block);
        }

        // ── The fallback close ───────────────────────────────────────────────

        [Test]
        public void AConditionalCloseDoesNotSuppressTheFallbackChip()
        {
            var doc = PaywallBlockDoc.Parse(RevnixJson.ParseObject(@"{""blocks"":[
                {""id"":""x"",""type"":""text"",""text"":""×"",""action"":""close"",""visibility"":""selected""}
            ]}"));
            Assert.IsFalse(RevnixPaywallClose.HasCloseAction(doc.Blocks));

            var certain = PaywallBlockDoc.Parse(RevnixJson.ParseObject(@"{""blocks"":[
                {""id"":""x"",""type"":""text"",""text"":""×"",""action"":""close""}
            ]}"));
            Assert.IsTrue(RevnixPaywallClose.HasCloseAction(certain.Blocks));
        }

        // ── Section 3: canvas maths ──────────────────────────────────────────

        [Test]
        public void CanvasScaleIsWidthOverDesignWidthCappedAtFourEighty()
        {
            Assert.AreEqual(1f, RevnixPaywallCanvasLayout.Scale(393f), 0.0001f);
            Assert.AreEqual(430f / 393f, RevnixPaywallCanvasLayout.Scale(430f), 0.0001f);
            Assert.AreEqual(480f / 393f, RevnixPaywallCanvasLayout.Scale(480f), 0.0001f);
            // A tablet never blows a phone design up 2.6×.
            Assert.AreEqual(480f / 393f, RevnixPaywallCanvasLayout.Scale(1024f), 0.0001f);
            Assert.AreEqual(1f, RevnixPaywallCanvasLayout.Scale(0f), 0.0001f);
        }

        [Test]
        public void ATallerViewportFillsAndAShorterOneScrolls()
        {
            // 393×1000: the design grows to 1000 tall, no band.
            Assert.AreEqual(1000f, RevnixPaywallCanvasLayout.DesignHeight(1000f, 1f), 0.0001f);
            // 393×700: the design keeps its 852 and scrolls.
            Assert.AreEqual(852f, RevnixPaywallCanvasLayout.DesignHeight(700f, 1f), 0.0001f);
            Assert.IsTrue(RevnixPaywallCanvasLayout.Overflows(700f, 852f));
            Assert.IsFalse(RevnixPaywallCanvasLayout.Overflows(852f, 852f));
            // 800×1200 tablet: scale caps at 480/393, and the design height
            // is the viewport in design units — scaled back, it fits exactly.
            var scale = RevnixPaywallCanvasLayout.Scale(800f);
            var design = RevnixPaywallCanvasLayout.DesignHeight(1200f, scale);
            Assert.AreEqual(1200f, RevnixPaywallCanvasLayout.ScaledHeight(design, scale), 0.001f);
            Assert.IsFalse(RevnixPaywallCanvasLayout.Overflows(1200f, RevnixPaywallCanvasLayout.ScaledHeight(design, scale)));
        }

        [Test]
        public void TheCloseChipMovesDownByTheStatusBarInCanvasUnits()
        {
            // A 2556px-tall screen whose safe rect starts 59px below the top,
            // on a canvas scaled 3×: 59 / 3 canvas units.
            Assert.AreEqual(59f / 3f, RevnixPaywallCanvasLayout.SafeAreaTopInset(2556f, 102f, 2556f - 102f - 59f, 3f), 0.0001f);
            // No notch, no inset; and never a negative one.
            Assert.AreEqual(0f, RevnixPaywallCanvasLayout.SafeAreaTopInset(2556f, 0f, 2556f, 3f), 0.0001f);
            Assert.AreEqual(0f, RevnixPaywallCanvasLayout.SafeAreaTopInset(2556f, 0f, 3000f, 3f), 0.0001f);
            // A zero scale factor is treated as 1 rather than dividing by it.
            Assert.AreEqual(59f, RevnixPaywallCanvasLayout.SafeAreaTopInset(2556f, 102f, 2556f - 102f - 59f, 0f), 0.0001f);
        }
    }
}
