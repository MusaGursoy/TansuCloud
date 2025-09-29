// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TansuCloud.E2E.Tests
{
    public class GatewayAliasTests
    {
        private static string GetGatewayBaseUrl()
        {
            return TestUrls.GatewayBaseUrl;
        }

        [Fact(DisplayName = "Gateway alias /Identity/Account/Login returns login form")]
        public async Task Gateway_LoginAlias_Returns_LoginForm()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // Arrange: no auto-redirect, accept dev certs.
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            var baseUrl = GetGatewayBaseUrl();
            var aliasUrl = $"{baseUrl}/Identity/Account/Login";

            // Tiny readiness: ensure gateway answers at root quickly.
            for (var i = 0; i < 6; i++)
            {
                try
                {
                    using var ping = await client.GetAsync($"{baseUrl}/", cts.Token);
                    if ((int)ping.StatusCode < 400)
                    {
                        break;
                    }
                }
                catch
                {
                    // ignore and retry
                }
                await Task.Delay(500, cts.Token);
            }

            // Act
            using var res = await client.GetAsync(aliasUrl, cts.Token);

            // Assert: either 200 with login form, or 3xx to canonical /identity path that contains the login form.
            if (res.StatusCode == HttpStatusCode.OK)
            {
                var html = await res.Content.ReadAsStringAsync(cts.Token);
                Assert.Contains("id=\"Input_Email\"", html);
                Assert.Contains("id=\"Input_Password\"", html);
                Assert.Contains("id=\"login-submit\"", html);
            }
            else if ((int)res.StatusCode >= 300 && (int)res.StatusCode < 400)
            {
                var location = res.Headers.Location;
                Assert.NotNull(location);
                Assert.StartsWith(
                    "/identity/Identity/Account/Login",
                    location!.AbsolutePath,
                    StringComparison.OrdinalIgnoreCase
                );

                // Follow once and verify form markers.
                var followUrl = location.IsAbsoluteUri
                    ? location.AbsoluteUri
                    : $"{baseUrl}{location}";
                using var res2 = await client.GetAsync(followUrl, cts.Token);
                Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
                var html2 = await res2.Content.ReadAsStringAsync(cts.Token);
                Assert.Contains("id=\"Input_Email\"", html2);
                Assert.Contains("id=\"Input_Password\"", html2);
                Assert.Contains("id=\"login-submit\"", html2);
            }
            else
            {
                Assert.Fail($"Unexpected status code: {(int)res.StatusCode} {res.StatusCode}");
            }
        } // End of Method Gateway_LoginAlias_Returns_LoginForm
    } // End of Class GatewayAliasTests
} // End of Namespace TansuCloud.E2E.Tests
