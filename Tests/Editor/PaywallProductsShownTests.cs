using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Revnix;

namespace Revnix.Tests
{
    public class PaywallProductsShownTests
    {
        private sealed class CaptureHttp : IRevnixHttp
        {
            public string Body;

            public Task<RevnixHttpResponse> Send(
                string method, string url, string jsonBody,
                IReadOnlyDictionary<string, string> headers, int timeoutMs)
            {
                if (url.EndsWith("/v1/paywalls/viewed")) Body = jsonBody;
                return Task.FromResult(new RevnixHttpResponse(200, "{}"));
            }
        }

        private static string Beacon(IReadOnlyList<RevnixPaywallProductShown> shown)
        {
            var http = new CaptureHttp();
            var client = new RevnixClient(new RevnixConfig
            {
                ApiKey = "rvx_pk_test",
                BaseUrl = "https://x",
                Http = http,
                Storage = new MemoryStorage(),
                Device = new DeviceFacts { Platform = "ios" },
            });
            client.LogPaywallDisplay("main", "pw_1", shown).GetAwaiter().GetResult();
            return http.Body;
        }

        [Test]
        public void ADisplayWithTwoProductsSendsThem()
        {
            var body = Beacon(new[]
            {
                new RevnixPaywallProductShown { ProductId = "annual", PriceLabel = "$29.99", AmountMinor = 2999, Currency = "USD" },
                new RevnixPaywallProductShown { ProductId = "monthly" },
            });

            StringAssert.Contains(
                "\"productsShown\":[{\"productId\":\"annual\",\"priceLabel\":\"$29.99\",\"amountMinor\":2999,\"currency\":\"USD\"},{\"productId\":\"monthly\"}]",
                body);
        }

        [Test]
        public void ADisplayWithNoProductsOmitsTheKey()
        {
            StringAssert.DoesNotContain("productsShown", Beacon(null));
            StringAssert.DoesNotContain("productsShown", Beacon(new List<RevnixPaywallProductShown>()));
        }

        [Test]
        public void EntriesAndLabelsAreTrimmedToTheServerCaps()
        {
            var many = new List<RevnixPaywallProductShown>();
            for (var i = 0; i < 25; i++)
                many.Add(new RevnixPaywallProductShown { ProductId = "p" + i, PriceLabel = new string('x', 80) });

            var body = Beacon(many);

            StringAssert.Contains("\"productId\":\"p19\"", body);
            StringAssert.DoesNotContain("\"productId\":\"p20\"", body);
            StringAssert.Contains("\"priceLabel\":\"" + new string('x', 64) + "\"", body);
            StringAssert.DoesNotContain(new string('x', 65), body);
        }
    }
}
