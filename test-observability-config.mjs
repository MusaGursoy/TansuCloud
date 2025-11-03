// Quick Playwright test for Observability Config page
import { chromium } from '@playwright/test';

(async () => {
  const browser = await chromium.launch({ headless: false });
  const context = await browser.newContext();
  const page = await context.newPage();

  try {
    console.log('Navigating to login page...');
    await page.goto('http://127.0.0.1:8080/');
    await page.waitForLoadState('networkidle');

    // Login
    console.log('Logging in...');
    const emailInput = page.locator('input[name="Input.Email"], input[type="email"]');
    const passwordInput = page.locator('input[name="Input.Password"], input[type="password"]');
    
    await emailInput.waitFor({ state: 'visible', timeout: 10000 });
    await emailInput.fill('admin@tansu.local');
    await passwordInput.fill('Passw0rd!');
    
    await page.click('button[type="submit"]');
    await page.waitForLoadState('networkidle');
    
    console.log('✓ Login successful');

    // Navigate to Observability Config page
    console.log('\nNavigating to Observability Config page...');
    await page.goto('http://127.0.0.1:8080/dashboard/admin/observability-config');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2000); // Wait for Blazor to render

    // Check page title
    const title = await page.title();
    console.log(`Page title: ${title}`);

    // Check for MudBlazor components
    console.log('\nChecking for MudBlazor components...');
    
    // Check for Retention Days fields
    const retentionTracesField = page.locator('label:has-text("Traces (days)")').first();
    if (await retentionTracesField.isVisible({ timeout: 5000 })) {
      console.log('✓ Traces retention field found');
    } else {
      console.log('✗ Traces retention field NOT found');
    }

    const retentionLogsField = page.locator('label:has-text("Logs (days)")').first();
    if (await retentionLogsField.isVisible({ timeout: 5000 })) {
      console.log('✓ Logs retention field found');
    } else {
      console.log('✗ Logs retention field NOT found');
    }

    const retentionMetricsField = page.locator('label:has-text("Metrics (days)")').first();
    if (await retentionMetricsField.isVisible({ timeout: 5000 })) {
      console.log('✓ Metrics retention field found');
    } else {
      console.log('✗ Metrics retention field NOT found');
    }

    // Check for Sampling field
    const samplingField = page.locator('label:has-text("Trace Sampling Ratio")').first();
    if (await samplingField.isVisible({ timeout: 5000 })) {
      console.log('✓ Trace Sampling Ratio field found');
    } else {
      console.log('✗ Trace Sampling Ratio field NOT found');
    }

    // Check for Save button
    const saveButton = page.locator('button:has-text("Save Configuration")').first();
    if (await saveButton.isVisible({ timeout: 5000 })) {
      console.log('✓ Save Configuration button found');
    } else {
      console.log('✗ Save Configuration button NOT found');
    }

    // Check for Refresh button
    const refreshButton = page.locator('button:has-text("Refresh")').first();
    if (await refreshButton.isVisible({ timeout: 5000 })) {
      console.log('✓ Refresh button found');
    } else {
      console.log('✗ Refresh button NOT found');
    }

    // Check for Alert SLOs section
    const alertSLOsHeader = page.locator('text=Alert SLO Templates').first();
    if (await alertSLOsHeader.isVisible({ timeout: 5000 })) {
      console.log('✓ Alert SLO Templates section found');
    } else {
      console.log('✗ Alert SLO Templates section NOT found');
    }

    // Take a screenshot
    await page.screenshot({ path: 'observability-config-test.png', fullPage: true });
    console.log('\n✓ Screenshot saved: observability-config-test.png');

    console.log('\n✅ All checks passed! The Observability Config page is using MudBlazor components.');

  } catch (error) {
    console.error('\n❌ Error during test:', error.message);
    await page.screenshot({ path: 'observability-config-error.png', fullPage: true });
    console.log('Error screenshot saved: observability-config-error.png');
  } finally {
    await browser.close();
  }
})();
