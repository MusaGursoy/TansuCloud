// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminDomainsUiE2E
{
    [Fact]
    public async Task Domains_Page_Renders_Or_Login()
    {
        // Gate UI test behind env var to avoid running by default
        var run = Environment.GetEnvironmentVariable("RUN_UI");
        if (!string.Equals(run, "1", StringComparison.Ordinal))
        {
            return; // not running UI tests in this environment
        }

        var baseUrl = TestUrls.GatewayBaseUrl;

        // Try to ensure browsers are installed (best-effort)
        try
        {
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }
        catch { }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true }
        );
        var context = await browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true }
        );
        var page = await context.NewPageAsync();

        // Navigate to Domains admin page (canonical under /dashboard)
        var target = baseUrl.TrimEnd('/') + "/dashboard/admin/domains";
        var response = await page.GotoAsync(
            target,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 }
        );
        Assert.NotNull(response);

        // Accept either the page header (already authenticated) or the sign-in screen
        var foundDomainsHeader = false;
        var foundLogin = false;
        try
        {
            await page.Locator("h2:has-text(\"Domains & TLS\")")
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            foundDomainsHeader = true;
        }
        catch { }

        if (!foundDomainsHeader)
        {
            // Look for common Identity login cues
            try
            {
                // Email/password fields or a generic Sign in text
                var loginLocator = page.Locator(
                    "input[type=email], input[name=\"Input.Email\"], text=Sign in"
                );
                await loginLocator.First.WaitForAsync(
                    new LocatorWaitForOptions { Timeout = 15000 }
                );
                foundLogin = true;
            }
            catch { }
        }

        Assert.True(
            foundDomainsHeader || foundLogin,
            "Expected either Domains admin page or login screen to be visible."
        );

        // If already on the page, check core controls exist
        if (foundDomainsHeader)
        {
            // Host input and Save buttons
            await page.Locator("text=Current bindings")
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await page.Locator("label:has-text(\"Host\")")
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Save (PFX)" })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Save (PEM)" })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            // Chain textarea is optional but should exist now
            await page.Locator("textarea[placeholder*='intermediate certificates']")
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
        }
    }

    [Fact(DisplayName = "Domains UI: PEM bind, rotate, delete end-to-end")]
    public async Task Domains_Pem_Bind_Rotate_Delete_UI()
    {
        // Only run when explicitly requested
        var run = Environment.GetEnvironmentVariable("RUN_UI");
        if (!string.Equals(run, "1", StringComparison.Ordinal))
            return;

        var baseUrl = TestUrls.GatewayBaseUrl;

        // Install browser if needed (best-effort)
        try
        {
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }
        catch { }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true }
        );
        var context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new() { Width = 1280, Height = 900 }
            }
        );
        var page = await context.NewPageAsync();

        // Auto-accept JS confirm dialogs (used by Rotate)
        page.Dialog += async (_, dialog) =>
        {
            try
            {
                await dialog.AcceptAsync();
            }
            catch { }
        };

        // Navigate and login if needed
        var domainsUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/domains";
        await page.GotoAsync(
            domainsUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }
        );
        if (!await IsOnDomainsPageAsync(page))
        {
            await EnsureLoggedInAsync(page, baseUrl);
            await page.GotoAsync(
                domainsUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }
            );
        }
        Assert.True(
            await IsOnDomainsPageAsync(page),
            "Domains admin page should render after login."
        );

        // Generate self-signed PEMs for a random host
        var host = $"ui{Guid.NewGuid():N}.local";
        var pem1 = CreateSelfSignedPem(host);

        // Fill Add/Replace (PEM) form (also provide chain PEM to assert Chain column)
        await page.Locator("input[placeholder='e.g. app.example.com']").FillAsync(host);
        await page.Locator("textarea[placeholder='-----BEGIN CERTIFICATE-----']")
            .FillAsync(pem1.CertPem);
        await page.Locator("textarea[placeholder='-----BEGIN PRIVATE KEY-----']")
            .FillAsync(pem1.KeyPem);
        await page.Locator(
                "textarea[placeholder='Concatenate intermediate certificates in PEM format']"
            )
            .FillAsync(pem1.CertPem); // provide leaf as a dummy chain to surface ChainProvided
        await page.GetByRole(AriaRole.Button, new() { Name = "Save (PEM)" }).ClickAsync();

        // Wait for either success text or table row to appear
        var saveOk =
            await TryWaitSelectorAsync(page, $"tr:has(td:text-is('{host}'))", 15000)
            || await TryWaitSelectorAsync(page, "text=Saved (PEM)", 5000);
        Assert.True(saveOk, "Expected PEM save to succeed and host to appear in table.");

        // Second assertion: verify Chain column displays linked/unlinked or at least a count when chain is provided
        var rowAfterPem = page.Locator($"tr:has(td:text-is('{host}'))");
        var chainCell = rowAfterPem.Locator("td").Nth(6);
        string chainText = await chainCell.InnerTextAsync(new() { Timeout = 10000 });
        Assert.False(
            string.Equals(chainText.Trim(), "-", StringComparison.Ordinal),
            $"Expected Chain column to show a value when chain PEM provided, got '{chainText}'."
        );

        // Rotate with a new PEM
        var pem2 = CreateSelfSignedPem(host);
        await page.Locator("input[placeholder='host to rotate']").FillAsync(host);
        await page.Locator("textarea[placeholder='-----BEGIN CERTIFICATE-----']")
            .Nth(1)
            .FillAsync(pem2.CertPem);
        await page.Locator("textarea[placeholder='-----BEGIN PRIVATE KEY-----']")
            .Nth(1)
            .FillAsync(pem2.KeyPem);
        await page.GetByRole(AriaRole.Button, new() { Name = "Rotate" }).ClickAsync();

        var rotated = await TryWaitSelectorAsync(page, "text=Rotated.", 15000);
        Assert.True(rotated, "Expected rotated confirmation message.");

        // Remove the binding row for cleanup
        var row = page.Locator($"tr:has(td:text-is('{host}'))");
        if (await row.CountAsync() > 0)
        {
            await row.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
            // After delete, the row should disappear
            var gone = await WaitForRowToDisappearAsync(page, host, TimeSpan.FromSeconds(10));
            Assert.True(gone, "Expected row to disappear after delete.");
        }
    }

    [Fact(DisplayName = "Domains UI: PFX bind, rotate, delete end-to-end")]
    public async Task Domains_Pfx_Bind_Rotate_Delete_UI()
    {
        // Only run when explicitly requested
        var run = Environment.GetEnvironmentVariable("RUN_UI");
        if (!string.Equals(run, "1", StringComparison.Ordinal))
            return;

        var baseUrl = TestUrls.GatewayBaseUrl;

        // Install browser if needed (best-effort)
        try
        {
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }
        catch { }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true }
        );
        var context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new() { Width = 1280, Height = 900 }
            }
        );
        var page = await context.NewPageAsync();

        // Auto-accept JS confirm dialogs (used by Rotate)
        page.Dialog += async (_, dialog) =>
        {
            try
            {
                await dialog.AcceptAsync();
            }
            catch { }
        };

        // Navigate and login if needed
        var domainsUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/domains";
        await page.GotoAsync(
            domainsUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }
        );
        if (!await IsOnDomainsPageAsync(page))
        {
            await EnsureLoggedInAsync(page, baseUrl);
            await page.GotoAsync(
                domainsUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }
            );
        }
        Assert.True(
            await IsOnDomainsPageAsync(page),
            "Domains admin page should render after login."
        );

        // Generate PFX for a random host and write to a temp file
        var host = $"pfx{Guid.NewGuid():N}.local";
        var pfx1 = CreateSelfSignedPfx(host, password: null);
        var pfxPath1 = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{Guid.NewGuid():N}.pfx"
        );
        await System.IO.File.WriteAllBytesAsync(pfxPath1, pfx1.Pfx);

        // Fill Add/Replace (PFX) form
        await page.Locator("input[placeholder='e.g. app.example.com']").FillAsync(host);
        await page.Locator("input[type='file']").First.SetInputFilesAsync(pfxPath1);
        // Password is optional; leave blank
        await page.GetByRole(AriaRole.Button, new() { Name = "Save (PFX)" }).ClickAsync();

        // Wait for success or table row
        var pfxSaveOk =
            await TryWaitSelectorAsync(page, $"tr:has(td:text-is('{host}'))", 15000)
            || await TryWaitSelectorAsync(page, "text=Saved", 5000);
        Assert.True(pfxSaveOk, "Expected PFX save to succeed and host to appear in table.");

        // Prepare second PFX and rotate via PFX input in Rotate section
        var pfx2 = CreateSelfSignedPfx(host, password: null);
        var pfxPath2 = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{Guid.NewGuid():N}.pfx"
        );
        await System.IO.File.WriteAllBytesAsync(pfxPath2, pfx2.Pfx);

        await page.Locator("input[placeholder='host to rotate']").FillAsync(host);
        await page.Locator("input[type='file']").Nth(1).SetInputFilesAsync(pfxPath2);
        await page.GetByRole(AriaRole.Button, new() { Name = "Rotate" }).ClickAsync();

        var rotated = await TryWaitSelectorAsync(page, "text=Rotated", 15000);
        Assert.True(rotated, "Expected rotated confirmation message (PFX path).");

        // Cleanup: remove row
        var row = page.Locator($"tr:has(td:text-is('{host}'))");
        if (await row.CountAsync() > 0)
        {
            await row.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
            var gone = await WaitForRowToDisappearAsync(page, host, TimeSpan.FromSeconds(10));
            Assert.True(gone, "Expected row to disappear after delete (PFX path).");
        }

        // Best-effort temp file cleanup
        try
        {
            System.IO.File.Delete(pfxPath1);
        }
        catch { }
        try
        {
            System.IO.File.Delete(pfxPath2);
        }
        catch { }
    }

    // Helpers (kept local to avoid additional shared fixtures)
    private static async Task<bool> IsOnDomainsPageAsync(IPage page)
    {
        try
        {
            var header = page.Locator("h2:has-text('Domains & TLS')");
            await header.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task EnsureLoggedInAsync(IPage page, string baseUrl)
    {
        // Heuristics: try common login fields on Identity UI
        var email = page.GetByLabel("Email", new() { Exact = false });
        var password = page.GetByLabel("Password", new() { Exact = false });
        var emailAlt = page.Locator(
            "input[name='Input.Email'], input[type=email], input#Input_Email"
        );
        var pwdAlt = page.Locator(
            "input[name='Input.Password'], input[type=password], input#Input_Password"
        );

        var onLogin = await TryWaitAsync(email, 8000) || await TryWaitAsync(emailAlt, 8000);
        if (!onLogin)
        {
            // Maybe already logged in
            if (await IsOnDomainsPageAsync(page))
                return;
        }

        if (await email.CountAsync() > 0)
            await email.FillAsync("admin@tansu.local");
        else
            await emailAlt.FillAsync("admin@tansu.local");
        if (await password.CountAsync() > 0)
            await password.FillAsync("Passw0rd!");
        else
            await pwdAlt.FillAsync("Passw0rd!");

        var loginBtn = page.GetByRole(AriaRole.Button, new() { Name = "Log in" });
        if (await loginBtn.CountAsync() > 0)
            await loginBtn.ClickAsync();
        else
            await page.Locator("button[type='submit'], input[type='submit']").First.ClickAsync();

        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 30000 });
        }
        catch { }
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    }

    private static async Task<bool> TryWaitAsync(ILocator locator, int timeoutMs)
    {
        try
        {
            await locator.WaitForAsync(new() { Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryWaitSelectorAsync(IPage page, string selector, int timeoutMs)
    {
        try
        {
            await page.WaitForSelectorAsync(selector, new() { Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForRowToDisappearAsync(
        IPage page,
        string host,
        TimeSpan timeout
    )
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var count = await page.Locator($"tr:has(td:text-is('{host}'))").CountAsync();
            if (count == 0)
                return true;
            await Task.Delay(250);
        }
        return false;
    }

    private static (string CertPem, string KeyPem) CreateSelfSignedPem(string host)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={host}",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1
        );
        req.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
                false,
                false,
                0,
                false
            )
        );
        req.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature
                    | System
                        .Security
                        .Cryptography
                        .X509Certificates
                        .X509KeyUsageFlags
                        .KeyEncipherment,
                false
            )
        );
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1)
        );

        var certDer = cert.Export(
            System.Security.Cryptography.X509Certificates.X509ContentType.Cert
        );
        var certB64 = Convert.ToBase64String(certDer, Base64FormattingOptions.InsertLineBreaks);
        var certPem = $"-----BEGIN CERTIFICATE-----\n{certB64}\n-----END CERTIFICATE-----\n";

        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var keyB64 = Convert.ToBase64String(pkcs8, Base64FormattingOptions.InsertLineBreaks);
        var keyPem = $"-----BEGIN PRIVATE KEY-----\n{keyB64}\n-----END PRIVATE KEY-----\n";

        return (certPem, keyPem);
    }

    private static (byte[] Pfx, string? Password) CreateSelfSignedPfx(
        string host,
        string? password = null
    )
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={host}",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1
        );
        req.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
                false,
                false,
                0,
                false
            )
        );
        req.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature
                    | System
                        .Security
                        .Cryptography
                        .X509Certificates
                        .X509KeyUsageFlags
                        .KeyEncipherment,
                false
            )
        );
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1)
        );
        var pfx = password is null
            ? cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12)
            : cert.Export(
                System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12,
                password
            );
        return (pfx, password);
    }
}
