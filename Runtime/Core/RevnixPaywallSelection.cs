// Selected context — the half of the designed-paywall render contract v2
// (REV-262) that decides WHAT to draw, kept apart from the UGUI code that
// draws it so it can be tested without an editor.
//
// Every renderer of a PaywallBlockDoc (the dashboard preview, React Native,
// iOS, Android, Flutter, Unity, Capacitor) answers three questions per block
// the same way:
//
//   * which package is SELECTED — host selection → the customer's tap → the
//     config highlight → the first package;
//   * whether a block is in SELECTED context — a card pinned to a package
//     (`packageIndex`) or a `repeat: "packages"` instance is, iff its package
//     is the selected one; its descendants inherit that; blocks outside any
//     package card never are;
//   * what that means — `selectedStyle` merges over `style` in selected
//     context, `visibility` hides the block in the wrong context, and copy
//     outside a package card resolves its tags against the selected package.
//
// The shared fixture tests/fixtures/paywall-selection-wire.json in revnix-app
// is the executable form of these rules; Tests/Editor/PaywallSelectionWireTests
// walks it through the functions below.

using System.Collections.Generic;

namespace Revnix
{
    /// <summary>Whether a block sits inside the selected plan card.</summary>
    public enum BlockSelectionContext
    {
        /// <summary>Outside any package card: selection fields are inert.</summary>
        None = 0,

        /// <summary>Inside the card of the selected package.</summary>
        Selected,

        /// <summary>Inside the card of a package that is not selected.</summary>
        Unselected,
    }

    /// <summary>One block as it is to be drawn in one place in the tree.</summary>
    public sealed class BlockResolution
    {
        /// <summary>The block, carrying its EFFECTIVE style (the selected
        /// merge already applied). The same instance as the document's when
        /// nothing merged.</summary>
        public PaywallBlock Block;

        /// <summary>The package this subtree describes — the pinned or
        /// repeated package, own or inherited. Null outside any package card.</summary>
        public BlockPackage Package;

        /// <summary>The package the block's copy resolves its tags against:
        /// <see cref="Package"/>, or the selected package at the root.</summary>
        public BlockPackage TagPackage;

        public BlockSelectionContext Context;

        /// <summary>False when <c>visibility</c> hides the block here. A hidden
        /// block's children are not drawn either.</summary>
        public bool Visible;

        /// <summary>The resolved copy of a text block, or a button's label.
        /// Null for every other kind.</summary>
        public string Text;
    }

    /// <summary>The selected-context rules of render contract v2.</summary>
    public static class RevnixPaywallSelection
    {
        public const string VisibilitySelected = "selected";
        public const string VisibilityUnselected = "unselected";

        /// <summary>The <c>repeat</c> value that draws a card once per package.</summary>
        public const string RepeatPackages = "packages";

        /// <summary>Whether the offering carries a package with this id.</summary>
        public static bool IsOffered(IList<BlockPackage> packages, string packageId)
            => Find(packages, packageId) != null;

        /// <summary>The offered package with this id, or null.</summary>
        public static BlockPackage Find(IList<BlockPackage> packages, string packageId)
        {
            if (packages == null || packageId == null) return null;
            for (var i = 0; i < packages.Count; i++)
            {
                var pkg = packages[i];
                if (pkg != null && pkg.PackageId == packageId) return pkg;
            }
            return null;
        }

        /// <summary>
        /// The selected package: the host's <c>selectedPackageId</c> when it
        /// names an offered package, else the renderer's own selection after a
        /// tap, else the config's highlight, else the first package. Null only
        /// when nothing is offered. The dashboard preview applies the same
        /// rule with only the highlight (it has no taps).
        /// </summary>
        public static string ResolveSelectedPackageId(
            IList<BlockPackage> packages,
            string hostSelectedId,
            string internalSelectedId,
            string highlightPackageId)
        {
            if (packages == null || packages.Count == 0) return null;
            if (IsOffered(packages, hostSelectedId)) return hostSelectedId;
            if (IsOffered(packages, internalSelectedId)) return internalSelectedId;
            if (IsOffered(packages, highlightPackageId)) return highlightPackageId;
            var first = packages[0];
            return first != null ? first.PackageId : null;
        }

        /// <summary>The selected package itself — <paramref name="selectedId"/>
        /// when offered, else the first package, else null.</summary>
        public static BlockPackage SelectedPackage(IList<BlockPackage> packages, string selectedId)
            => Find(packages, ResolveSelectedPackageId(packages, selectedId, null, null));

        /// <summary>
        /// The package a block's tags resolve against: the package its subtree
        /// describes, or — outside any package card — the selected package.
        /// This is what makes "7 days free, then {price}/{period_short}" at
        /// the root of a design follow the customer's selection instead of
        /// drawing the literal tag.
        /// </summary>
        public static BlockPackage TagPackage(
            BlockPackage contextPackage,
            IList<BlockPackage> packages,
            string selectedId)
            => contextPackage ?? SelectedPackage(packages, selectedId);

        /// <summary>The context a subtree describing <paramref name="contextPackage"/> is in.</summary>
        public static BlockSelectionContext ContextFor(BlockPackage contextPackage, string selectedId)
        {
            if (contextPackage == null) return BlockSelectionContext.None;
            return selectedId != null && contextPackage.PackageId == selectedId
                ? BlockSelectionContext.Selected
                : BlockSelectionContext.Unselected;
        }

        /// <summary>
        /// The style a block draws with in a context: <c>selectedStyle</c>
        /// merged over <c>style</c> (other wins, field by field) in selected
        /// context, the plain style otherwise. Returns the block's own style
        /// instance when nothing merges.
        /// </summary>
        public static BlockStyle EffectiveStyle(PaywallBlock block, BlockSelectionContext context)
        {
            if (block == null) return null;
            if (context != BlockSelectionContext.Selected || block.SelectedStyle == null) return block.Style;
            return (block.Style ?? new BlockStyle()).Merging(block.SelectedStyle);
        }

        /// <summary>
        /// Whether a block draws in a context. <c>visibility</c> is inert
        /// outside any package card — a root-level block is never hidden — and
        /// a value this SDK does not know draws the block rather than losing it.
        /// </summary>
        public static bool IsVisible(PaywallBlock block, BlockSelectionContext context)
        {
            if (block == null) return false;
            var visibility = block.Visibility;
            if (visibility == null || context == BlockSelectionContext.None) return true;
            if (visibility == VisibilitySelected) return context == BlockSelectionContext.Selected;
            if (visibility == VisibilityUnselected) return context == BlockSelectionContext.Unselected;
            return true;
        }

        /// <summary>Whether this card draws once per package.</summary>
        public static bool IsRepeat(PaywallBlock block)
            => block != null && block.Kind == PaywallBlockKind.Card && block.Repeat == RepeatPackages;

        /// <summary>Whether this card is pinned to one package by index.</summary>
        public static bool IsPinned(PaywallBlock block)
            => block != null && block.Kind == PaywallBlockKind.Card
                && !IsRepeat(block) && block.PackageIndex.HasValue;

        /// <summary>
        /// Resolves a block where it sits in the tree. A pinned card decides
        /// its own context from its own package; everything else inherits the
        /// enclosing package card's. Returns null for a pinned card whose index
        /// the offering does not reach — it is dropped rather than drawn with
        /// unresolved tags, the same as every other renderer.
        /// </summary>
        public static BlockResolution Resolve(
            PaywallBlock block,
            BlockPackage inheritedPackage,
            IList<BlockPackage> packages,
            string selectedId)
        {
            if (block == null) return null;
            var package = inheritedPackage;
            if (IsPinned(block))
            {
                var index = block.PackageIndex.Value;
                if (packages == null || index < 0 || index >= packages.Count) return null;
                package = packages[index];
            }
            return Build(block, package, packages, selectedId);
        }

        /// <summary>One instance of a <c>repeat: "packages"</c> card, drawn for
        /// <paramref name="instancePackage"/> (null when nothing is offered and
        /// the design still draws once).</summary>
        public static BlockResolution ResolveInstance(
            PaywallBlock block,
            BlockPackage instancePackage,
            IList<BlockPackage> packages,
            string selectedId)
        {
            if (block == null) return null;
            return Build(block, instancePackage, packages, selectedId);
        }

        private static BlockResolution Build(
            PaywallBlock block,
            BlockPackage package,
            IList<BlockPackage> packages,
            string selectedId)
        {
            var context = ContextFor(package, selectedId);
            var tagPackage = TagPackage(package, packages, selectedId);
            var styled = block.WithStyle(EffectiveStyle(block, context));
            string text = null;
            if (block.Kind == PaywallBlockKind.Text)
            {
                text = RevnixPaywallTags.Resolve(block.Text ?? "", tagPackage, packages);
            }
            else if (block.Kind == PaywallBlockKind.Button)
            {
                text = RevnixPaywallTags.Resolve(block.Label ?? "", tagPackage, packages);
            }
            return new BlockResolution
            {
                Block = styled,
                Package = package,
                TagPackage = tagPackage,
                Context = context,
                Visible = IsVisible(block, context),
                Text = text,
            };
        }

        /// <summary>
        /// Every block of the document as it would be drawn, in draw order —
        /// the pure walk the UGUI renderer mirrors, and what the wire-fixture
        /// test asserts against. A repeated card contributes one resolution per
        /// instance (so its id may appear more than once); a hidden block is
        /// listed with <see cref="BlockResolution.Visible"/> false and its
        /// children are not; a pinned card the offering does not reach is
        /// absent altogether.
        /// </summary>
        public static List<BlockResolution> ResolveTree(
            PaywallBlockDoc doc,
            IList<BlockPackage> packages,
            string selectedId)
        {
            var out_ = new List<BlockResolution>();
            if (doc == null || doc.Blocks == null) return out_;
            foreach (var block in doc.Blocks) Walk(block, null, packages, selectedId, out_);
            return out_;
        }

        private static void Walk(
            PaywallBlock block,
            BlockPackage inherited,
            IList<BlockPackage> packages,
            string selectedId,
            List<BlockResolution> out_)
        {
            if (block == null) return;
            if (IsRepeat(block))
            {
                if (packages == null || packages.Count == 0)
                {
                    var single = ResolveInstance(block, null, packages, selectedId);
                    if (single == null) return;
                    out_.Add(single);
                    if (single.Visible) WalkChildren(block, null, packages, selectedId, out_);
                    return;
                }
                foreach (var each in packages)
                {
                    var instance = ResolveInstance(block, each, packages, selectedId);
                    if (instance == null) continue;
                    out_.Add(instance);
                    if (instance.Visible) WalkChildren(block, each, packages, selectedId, out_);
                }
                return;
            }

            var resolved = Resolve(block, inherited, packages, selectedId);
            if (resolved == null) return;
            out_.Add(resolved);
            if (resolved.Visible) WalkChildren(block, resolved.Package, packages, selectedId, out_);
        }

        private static void WalkChildren(
            PaywallBlock block,
            BlockPackage package,
            IList<BlockPackage> packages,
            string selectedId,
            List<BlockResolution> out_)
        {
            if (block.Kind != PaywallBlockKind.Card || block.Children == null) return;
            foreach (var child in block.Children) Walk(child, package, packages, selectedId, out_);
        }
    }
}
