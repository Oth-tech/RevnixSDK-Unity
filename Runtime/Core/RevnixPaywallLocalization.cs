using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Revnix
{
    // REV-271: paywall localization. A designed paywall carries ONE tree plus
    // a side table of translated strings — never one tree per language — so
    // styles, layout and block ids are shared and only words differ.
    //
    // Selection happens HERE, at render, rather than server-side at resolve.
    // The resolution is cached on the device, so a locale chosen by the server
    // would pin a cached paywall to whatever language it was fetched in:
    // change the device's language and the old copy would keep rendering until
    // the cache expired, and offline it would never change at all.
    //
    // Mirrored across all six Revnix SDKs — keep the key scheme, the fallback
    // chain and the field list identical.

    /// <summary>The translations published with a document.</summary>
    public sealed class PaywallLocalization
    {
        /// <summary>The language the tree's own copy is written in. Never a
        /// key in <see cref="Tables"/>.</summary>
        public string DefaultLocale;

        /// <summary>BCP-47 tag → (<c>&lt;blockId&gt;.&lt;path&gt;</c> →
        /// translated string).</summary>
        public Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>();

        public bool IsEmpty { get { return Tables.Count == 0; } }

        /// <summary>
        /// Reads <c>defaultLocale</c> / <c>locales</c> off a raw block
        /// document. A malformed table costs the TRANSLATIONS, never the
        /// paywall — the same forgiveness every other optional field in the
        /// parser gets.
        /// </summary>
        public static PaywallLocalization Parse(Dictionary<string, object> map)
        {
            var result = new PaywallLocalization();
            if (map == null) return result;
            result.DefaultLocale = RevnixJson.GetString(map, "defaultLocale");
            var raw = RevnixJson.GetObject(map, "locales");
            if (raw == null) return result;
            foreach (var pair in raw)
            {
                // Normalized on the way IN so a hand-written "es_mx" in the
                // catalog still matches a device reporting "es-MX".
                var tag = RevnixLocale.Normalize(pair.Key);
                if (tag == null) continue;
                var table = pair.Value as Dictionary<string, object>;
                if (table == null) continue;
                var strings = new Dictionary<string, string>();
                foreach (var cell in table)
                {
                    var text = cell.Value as string;
                    if (text != null) strings[cell.Key] = text;
                }
                if (strings.Count > 0) result.Tables[tag] = strings;
            }
            return result;
        }
    }

    /// <summary>Locale tag handling and the fallback chain.</summary>
    public static class RevnixLocale
    {
        /// <summary>
        /// BCP-47, hyphenated: "es_MX" and "es-mx" both normalize to "es-MX".
        /// Applied to authored tags and to the device's own locale alike, so
        /// the two can never miss each other over punctuation or case. Null
        /// for anything that is not a language tag, which keeps junk out of
        /// the lookup instead of into it.
        /// </summary>
        public static string Normalize(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            var parts = tag.Trim().Replace('_', '-')
                .Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var language = parts[0].ToLowerInvariant();
            if (language.Length < 2 || language.Length > 3) return null;
            foreach (var c in language) if (c < 'a' || c > 'z') return null;

            var sb = new StringBuilder(language);
            for (var i = 1; i < parts.Length; i++)
            {
                var part = parts[i];
                sb.Append('-');
                // Region subtags are uppercase ("MX", "419"), scripts title
                // case ("Hans"); anything longer is a variant, kept lowercase.
                // A subtag outside BCP-47's shapes makes the WHOLE tag junk
                // rather than passing through — otherwise a device reporting
                // "es-!!" would look up a language.
                if (part.Length == 2 && IsAlpha(part)) sb.Append(part.ToUpperInvariant());
                else if (part.Length == 3 && IsDigits(part)) sb.Append(part);
                else if (part.Length == 4 && IsAlpha(part))
                    sb.Append(char.ToUpperInvariant(part[0]))
                      .Append(part.Substring(1).ToLowerInvariant());
                else if (part.Length >= 5 && part.Length <= 8 && IsAlphanumeric(part))
                    sb.Append(part.ToLowerInvariant());
                else return null;
            }
            return sb.ToString();
        }

        private static bool IsAlpha(string s)
        {
            foreach (var c in s) if (!char.IsLetter(c) || c > 127) return false;
            return true;
        }

        private static bool IsAlphanumeric(string s)
        {
            foreach (var c in s)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok) return false;
            }
            return true;
        }

        private static bool IsDigits(string s)
        {
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private static string BaseLanguage(string tag)
        {
            var dash = tag.IndexOf('-');
            return dash < 0 ? tag : tag.Substring(0, dash);
        }

        /// <summary>
        /// Which tables to consult, most specific first.
        ///
        /// "es-MX" on a paywall translated into "es" reads the "es" table: a
        /// regional variant that was never authored falls back to its language
        /// rather than to the source, which is the difference between a
        /// Mexican customer reading Spanish and reading English. The authored
        /// tree is the last resort and is deliberately NOT in this chain — the
        /// lookup falls through to it.
        /// </summary>
        public static List<string> Chain(
            ICollection<string> available, string locale, string defaultLocale)
        {
            var chain = new List<string>();
            Action<string> push = tag =>
            {
                if (tag != null && available.Contains(tag) && !chain.Contains(tag))
                    chain.Add(tag);
            };
            var wanted = Normalize(locale);
            if (wanted != null)
            {
                push(wanted);
                push(BaseLanguage(wanted));
                // "es" asked for, only "es-MX" authored: one regional table
                // beats the source language, and the first SORTED match keeps
                // the choice deterministic across devices rather than
                // dictionary-order dependent.
                if (chain.Count == 0)
                {
                    var language = BaseLanguage(wanted);
                    var regional = new List<string>();
                    foreach (var tag in available)
                        if (BaseLanguage(tag) == language) regional.Add(tag);
                    regional.Sort(StringComparer.Ordinal);
                    if (regional.Count > 0) push(regional[0]);
                }
            }
            push(Normalize(defaultLocale));
            return chain;
        }

        /// <summary>
        /// The device's language. <c>CurrentUICulture</c> is what Unity sets
        /// from the system language, so a paywall matching it matches whatever
        /// the rest of the game localizes to.
        /// </summary>
        public static string DeviceLocale()
        {
            try
            {
                var name = CultureInfo.CurrentUICulture.Name;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the document with every string swapped for
        /// <paramref name="locale"/>'s.
        ///
        /// Applied ONCE before rendering rather than at each text node: the
        /// view then needs no localization awareness at all, and the six SDKs
        /// cannot drift on which fields are translatable. Returns
        /// <paramref name="doc"/> untouched when nothing applies, so an
        /// untranslated paywall costs nothing.
        ///
        /// The blocks are COPIED rather than edited in place: the parsed
        /// document may be reused across renders, and overwriting its strings
        /// would make the first language rendered permanent.
        ///
        /// <c>{price}</c> and the other copy tags survive, because they are
        /// resolved AFTER this on the localized string — "Solo {price} al mes"
        /// works.
        /// </summary>
        public static PaywallBlockDoc Localize(PaywallBlockDoc doc, string locale)
        {
            if (doc == null || doc.Localization == null || doc.Localization.IsEmpty) return doc;
            var tables = doc.Localization.Tables;
            var chain = Chain(tables.Keys, locale, doc.Localization.DefaultLocale);
            if (chain.Count == 0) return doc;

            Func<string, string, string> lookup = (key, authored) =>
            {
                foreach (var tag in chain)
                {
                    Dictionary<string, string> table;
                    if (!tables.TryGetValue(tag, out table)) continue;
                    string value;
                    // An empty translation means "not translated", never
                    // "render nothing": a blank CTA is a dead paywall, and
                    // export/import round-trips leave empty cells behind for
                    // every untouched row.
                    if (table.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
                        return value;
                }
                return authored;
            };

            var copy = doc.ShallowCopy();
            copy.Blocks = LocalizeAll(doc.Blocks, lookup);
            return copy;
        }

        private static List<PaywallBlock> LocalizeAll(
            List<PaywallBlock> blocks, Func<string, string, string> lookup)
        {
            var result = new List<PaywallBlock>();
            if (blocks == null) return result;
            foreach (var block in blocks) result.Add(LocalizeBlock(block, lookup));
            return result;
        }

        private static PaywallBlock LocalizeBlock(
            PaywallBlock block, Func<string, string, string> lookup)
        {
            if (block == null) return null;
            var copy = block.ShallowCopy();
            var id = block.Id ?? "";
            switch (block.Kind)
            {
                case PaywallBlockKind.Text:
                    if (copy.Text != null) copy.Text = lookup(id + ".text", copy.Text);
                    break;
                case PaywallBlockKind.Button:
                    if (copy.Label != null) copy.Label = lookup(id + ".label", copy.Label);
                    break;
                case PaywallBlockKind.List:
                    if (copy.Items != null)
                    {
                        var items = new List<BlockListItem>();
                        for (var i = 0; i < copy.Items.Count; i++)
                        {
                            var item = copy.Items[i];
                            items.Add(new BlockListItem
                            {
                                Icon = item.Icon,
                                Title = item.Title == null
                                    ? null
                                    : lookup(id + ".items." + i + ".title", item.Title),
                                Description = item.Description == null
                                    ? null
                                    : lookup(id + ".items." + i + ".description", item.Description),
                            });
                        }
                        copy.Items = items;
                    }
                    break;
                case PaywallBlockKind.Products:
                    if (copy.TitleTpl != null) copy.TitleTpl = lookup(id + ".titleTpl", copy.TitleTpl);
                    if (copy.PriceTpl != null) copy.PriceTpl = lookup(id + ".priceTpl", copy.PriceTpl);
                    if (copy.HighlightSub != null)
                        copy.HighlightSub = lookup(id + ".highlightSub", copy.HighlightSub);
                    if (copy.BadgeText != null)
                        copy.BadgeText = lookup(id + ".badgeText", copy.BadgeText);
                    break;
                case PaywallBlockKind.Card:
                    copy.Children = LocalizeAll(copy.Children, lookup);
                    break;
            }
            return copy;
        }
    }
}
