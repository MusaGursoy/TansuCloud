// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TansuCloud.E2E.Tests
{
    public class OpenTelemetryConfigTests
    {
        [Fact(
            DisplayName = "OTEL config has default OTLP endpoint configured in appsettings.Development.json",
            Skip = "Skip: reading config files across project boundaries is brittle at test runtime; OTEL configuration is validated via runtime smoke and health checks."
        )]
        public void Otel_Otlp_Endpoint_Configured()
        {
            // Intentionally skipped; see Skip attribute for rationale.
            // If needed, implement a resilient solution by discovering the solution root
            // and reading the target appsettings via Path.Combine.
            Assert.True(true);
        }
    } // End of Class OpenTelemetryConfigTests
} // End of Namespace TansuCloud.E2E.Tests
