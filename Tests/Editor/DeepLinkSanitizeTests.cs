using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class DeepLinkSanitizeTests
    {
        [TestCase("myapp://promo/x?utm_source=a&token=secret&GCLID=Z&revnix_link=1#frag", "myapp://promo/x?utm_source=a&GCLID=Z&revnix_link=1")]
        [TestCase("https://user:pw@example.com:8080/p?email=a@b.c&pid=9", "https://example.com:8080/p?pid=9")]
        [TestCase("myapp://promo?token=secret", "myapp://promo")]
        [TestCase("https://example.com/a@b#x", "https://example.com/a@b")]
        [TestCase("myapp://p?utm_id=1&campaign_id=2&x=3", "myapp://p?utm_id=1&campaign_id=2")]
        [TestCase("https://x.com/p?utm_campaign=a%2Bb&t=1", "https://x.com/p?utm_campaign=a%2Bb")]
        [TestCase("myapp:open/https://cdn.x/a@b/c?pid=1", "myapp:open/https://cdn.x/a@b/c?pid=1")]
        [TestCase("myapp://p?utm%5Fsource=a%20b&t=1", "myapp://p?utm%5Fsource=a%20b")]
        [TestCase("  myapp://p?pid=1#f \n", "myapp://p?pid=1")]
        public void KeepsOnlyAttributionParams(string input, string expected)
        {
            Assert.AreEqual(expected, RevnixClient.SanitizeDeepLinkUrl(input));
        }
    }
}
