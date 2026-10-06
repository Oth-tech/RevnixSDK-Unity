// REV-271 designed-paywall localization.
//
// The dashboard writes the table; this SDK reads it. These are the parity
// contract — the same cases run in revnix-app and in every other Revnix SDK,
// so a paywall translated in the builder resolves identically everywhere.
//
// The guarantees that matter are the ones a shipped game cannot be patched out
// of: an untranslated string must never render blank, a regional locale must
// reach its language, and the overlay must disturb nothing but words.
//
// Everything asserted here is in Runtime/Core, which is deliberately free of
// UnityEngine — the UGUI renderer only consumes what these functions produce.

using System.Collections.Generic;
using NUnit.Framework;
using Revnix;
using Revnix.Unity;

namespace Revnix.Tests
{
    public class PaywallLocalizationTests
    {
        [TearDown]
        public void ClearLocaleOverride()
        {
            RevnixSdk.SetLocale(null);
        }

        private const string Json = @"{
          ""version"": 1, ""background"": ""#101014"", ""textColor"": ""#F5F7FA"",
          ""accent"": ""#6478ff"", ""accentInk"": ""#0B0D10"",
          ""defaultLocale"": ""en"",
          ""locales"": {
            ""es"": {
              ""hed.text"": ""Desbloquea Pro"",
              ""plans.priceTpl"": ""{price}/mes"",
              ""cta.label"": ""Continuar""
            },
            ""pt_br"": { ""hed.text"": ""Desbloqueie o Pro"" }
          },
          ""blocks"": [
            { ""id"": ""hed"", ""type"": ""text"", ""text"": ""Unlock Pro"", ""style"": { ""fontSize"": 28 } },
            { ""id"": ""sub"", ""type"": ""text"", ""text"": ""No ads"" },
            { ""id"": ""wrap"", ""type"": ""card"", ""layout"": ""column"", ""children"": [
                { ""id"": ""plans"", ""type"": ""products"", ""titleTpl"": ""{title}"", ""priceTpl"": ""{price}/mo"" },
                { ""id"": ""cta"", ""type"": ""button"", ""label"": ""Continue"" } ] }
          ]
        }";

        private static PaywallBlockDoc Doc(string json = Json)
        {
            var doc = PaywallBlockDoc.Parse(RevnixJson.Parse(json));
            Assert.IsNotNull(doc);
            return doc;
        }

        private static PaywallBlock Child(PaywallBlockDoc doc, int card, int index)
        {
            return doc.Blocks[card].Children[index];
        }

        // ——— Tags ———

        [Test]
        public void NormalizesTagsToOneCanonicalForm()
        {
            Assert.AreEqual("es-MX", RevnixLocale.Normalize("es_mx"));
            Assert.AreEqual("pt-BR", RevnixLocale.Normalize(" PT-br "));
            Assert.AreEqual("zh-Hans-CN", RevnixLocale.Normalize("zh-hans-cn"));
            Assert.AreEqual("es-419", RevnixLocale.Normalize("es-419"));
            // Junk must not become a language nobody can select.
            Assert.IsNull(RevnixLocale.Normalize("english"));
            Assert.IsNull(RevnixLocale.Normalize(""));
        }

        [Test]
        public void AuthoredTagsAreNormalizedOnParse()
        {
            // "pt_br" was hand-written in the catalog; a device reporting
            // "pt-BR" must still find it.
            var doc = RevnixLocale.Localize(Doc(), "pt-BR");
            Assert.AreEqual("Desbloqueie o Pro", doc.Blocks[0].Text);
        }

        // ——— Fallback chain ———

        [Test]
        public void RegionalLocaleFallsBackToItsLanguageNotToTheSource()
        {
            var available = new List<string> { "es", "fr" };
            CollectionAssert.AreEqual(
                new[] { "es" }, RevnixLocale.Chain(available, "es-MX", "en"));
            // Deterministic across devices: dictionary order must not decide
            // what a customer reads.
            CollectionAssert.AreEqual(
                new[] { "es-AR" },
                RevnixLocale.Chain(new List<string> { "es-MX", "es-AR" }, "es", null));
            CollectionAssert.IsEmpty(
                RevnixLocale.Chain(new List<string> { "es" }, "ja", null));
        }

        // ——— Applying a language ———

        [Test]
        public void SwapsStringsAtEveryDepthAndKeepsCopyTags()
        {
            var doc = RevnixLocale.Localize(Doc(), "es-MX");
            Assert.AreEqual("Desbloquea Pro", doc.Blocks[0].Text);
            Assert.AreEqual("Continuar", Child(doc, 2, 1).Label);
            // The tag survives translation, so {price} still resolves after it.
            Assert.AreEqual("{price}/mes", Child(doc, 2, 0).PriceTpl);
            Assert.AreEqual("{title}", Child(doc, 2, 0).TitleTpl);
        }

        [Test]
        public void UntranslatedStringsKeepTheAuthoredCopy()
        {
            var doc = RevnixLocale.Localize(Doc(), "es");
            Assert.AreEqual("No ads", doc.Blocks[1].Text);
        }

        [Test]
        public void EmptyTranslationMeansUntranslatedNotBlank()
        {
            // An export/import round-trip leaves empty cells everywhere;
            // honouring them would ship a paywall with no CTA label.
            var blanked = Json.Replace(@"""cta.label"": ""Continuar""", @"""cta.label"": """"");
            var doc = RevnixLocale.Localize(Doc(blanked), "es");
            Assert.AreEqual("Continue", Child(doc, 2, 1).Label);
        }

        [Test]
        public void OnlyWordsChange()
        {
            var original = Doc();
            var doc = RevnixLocale.Localize(original, "es");
            Assert.AreEqual(original.Accent, doc.Accent);
            Assert.AreEqual(original.Background, doc.Background);
            Assert.AreEqual("hed", doc.Blocks[0].Id);
            Assert.AreEqual(28d, doc.Blocks[0].Style.FontSize);
            // The parsed document is reused across renders, so localizing must
            // COPY — overwriting in place would make the first language drawn
            // permanent.
            Assert.AreEqual("Unlock Pro", original.Blocks[0].Text);
        }

        [Test]
        public void UnmatchedLocaleRendersTheAuthoredDocument()
        {
            Assert.AreEqual("Unlock Pro", RevnixLocale.Localize(Doc(), "ja").Blocks[0].Text);
        }

        [Test]
        public void MalformedTableCostsTheTranslationsNeverThePaywall()
        {
            const string broken = @"{""version"":1,""background"":""#000"",""textColor"":""#fff"",
             ""accent"":""#6478ff"",""accentInk"":""#fff"",""locales"":""nonsense"",
             ""blocks"":[{""id"":""hed"",""type"":""text"",""text"":""Unlock Pro""}]}";
            var doc = Doc(broken);
            Assert.IsTrue(doc.Localization.IsEmpty);
            Assert.AreEqual("Unlock Pro", RevnixLocale.Localize(doc, "es").Blocks[0].Text);
        }

        [Test]
        public void LinkLabelsResolveByLanguage()
        {
            Assert.AreEqual(("بحال کریں", "شرائط", "رازداری"), RevnixLocale.LinkLabels("ur"));
            Assert.AreEqual(("بحال کریں", "شرائط", "رازداری"), RevnixLocale.LinkLabels("ur-PK"));
            Assert.AreEqual(("Restaurar", "Termos", "Privacidade"), RevnixLocale.LinkLabels("pt_BR"));
        }

        [Test]
        public void LinkLabelsPickMandarinScriptByRegion()
        {
            var traditional = ("恢復購買", "條款", "隱私");
            var simplified = ("恢复购买", "条款", "隐私");
            Assert.AreEqual(traditional, RevnixLocale.LinkLabels("zh-Hant-TW"));
            Assert.AreEqual(traditional, RevnixLocale.LinkLabels("zh-TW"));
            Assert.AreEqual(traditional, RevnixLocale.LinkLabels("zh-HK"));
            Assert.AreEqual(simplified, RevnixLocale.LinkLabels("zh-Hans-HK"));
            Assert.AreEqual(simplified, RevnixLocale.LinkLabels("zh-CN"));
            Assert.AreEqual(simplified, RevnixLocale.LinkLabels("zh"));
        }

        [Test]
        public void LinkLabelsApplyAliasesAndFallBackToEnglish()
        {
            var english = ("Restore", "Terms", "Privacy");
            Assert.AreEqual(("שחזור", "תנאים", "פרטיות"), RevnixLocale.LinkLabels("iw"));
            Assert.AreEqual(("Gjenopprett", "Vilkår", "Personvern"), RevnixLocale.LinkLabels("no"));
            Assert.AreEqual(english, RevnixLocale.LinkLabels("xx"));
            Assert.AreEqual(english, RevnixLocale.LinkLabels(null));
            Assert.AreEqual(english, RevnixLocale.LinkLabels(""));
        }

        [Test]
        public void LocalizeSetsDefaultLocaleToTheChainsTopPick()
        {
            const string json = @"{
              ""version"": 1, ""background"": ""#000"", ""textColor"": ""#fff"",
              ""accent"": ""#6478ff"", ""accentInk"": ""#fff"",
              ""locales"": { ""es"": { ""hed.text"": ""Hola"" } },
              ""blocks"": [ { ""id"": ""hed"", ""type"": ""text"", ""text"": ""Hi"" } ]
            }";
            var doc = RevnixLocale.Localize(Doc(json), "es-MX");
            Assert.AreEqual("es", doc.Localization.DefaultLocale);
        }

        [Test]
        public void SetLocaleOverridesTheDeviceLocale()
        {
            RevnixSdk.SetLocale("ur");
            Assert.AreEqual("ur", RevnixLocale.DeviceLocale());
        }
    }
}
