$ErrorActionPreference = 'Stop'

# Create a C# script file
$csCode = @'
using Microsoft.Playwright;
using System;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        
        try
        {
            Console.WriteLine("[INFO] Navigating to login page...");
            await page.GotoAsync("http://127.0.0.1:8080/dashboard", new PageGotoOptions { Timeout = 30000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            
            Console.WriteLine("[INFO] Logging in...");
            var emailField = page.Locator("input[name='Input.Email'], #Input_Email");
            await emailField.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await emailField.FillAsync("admin@tansu.local");
            
            var passwordField = page.Locator("input[name='Input.Password'], #Input_Password, input[type='password']");
            await passwordField.FillAsync("Passw0rd!");
            
            var submitButton = page.Locator("button[type='submit'], input[type='submit']").First;
            await submitButton.ClickAsync();
            
            await page.WaitForURLAsync("**/dashboard**", new PageWaitForURLOptions { Timeout = 15000 });
            Console.WriteLine("[OK] Login successful");
            
            Console.WriteLine("[INFO] Navigating to tenant page...");
            await page.GotoAsync("http://127.0.0.1:8080/dashboard/tenant/acme-dev", new PageGotoOptions { Timeout = 30000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(3000);
            
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = "tenant-page.png", FullPage = true });
            Console.WriteLine("[OK] Screenshot saved to tenant-page.png");
            
            var title = await page.TitleAsync();
            Console.WriteLine($"Page title: {title}");
            
            var headings = await page.Locator("h1, h2, h3, h4, h5, h6").AllAsync();
            Console.WriteLine($"Found {headings.Count} headings:");
            foreach (var h in headings)
            {
                var text = await h.TextContentAsync();
                var tag = await h.EvaluateAsync<string>("el => el.tagName");
                Console.WriteLine($"  {tag}: '{text}'");
            }
            
            var html = await page.ContentAsync();
            var preview = html.Length > 8000 ? html.Substring(0, 8000) : html;
            Console.WriteLine($"\nPage HTML (first 8000 chars):\n{preview}");
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"Stack: {ex.StackTrace}");
            try {
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = "error-page.png", FullPage = true });
                Console.WriteLine("Error screenshot saved to error-page.png");
            } catch { }
        }
        finally
        {
            await browser.CloseAsync();
        }
    }
}
'@

$csCode | Out-File -FilePath "$PSScriptRoot\check.csx" -Encoding UTF8

Write-Host "Running Playwright diagnostic..."
dotnet script "$PSScriptRoot\check.csx"

Remove-Item "$PSScriptRoot\check.csx" -ErrorAction SilentlyContinue
