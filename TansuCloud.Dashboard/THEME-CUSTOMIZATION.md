# TansuCloud Dashboard Theme Customization Guide

## Overview

TansuCloud Dashboard uses **MudBlazor** (MIT License - 100% open-source) for a professional, Material Design-based UI. All colors, typography, and spacing are centralized in `Services/ThemeService.cs` for consistent branding across Admin and Tenant pages.

## Current Brand Colors

### Light Mode (Default)

- **Primary**: `#594AE2` - TansuCloud brand purple
- **Secondary**: `#26C6DA` - Cyan accent
- **AppBar**: `#594AE2` - Purple top bar
- **Background**: `#F5F5F7` - Light gray page background
- **Surface**: `#FFFFFF` - White cards and panels
- **Success**: `#4CAF50` - Green (confirmations, success states)
- **Error**: `#F44336` - Red (errors, warnings)
- **Warning**: `#FF9800` - Orange (cautions)
- **Info**: `#2196F3` - Blue (information)

### Dark Mode

- **Primary**: `#7C6FE8` - Lighter purple
- **Secondary**: `#26C6DA` - Same cyan
- **AppBar/Surface**: `#1E1E2E` - Dark blue-gray
- **Background**: `#121218` - Almost black

## How to Change Brand Colors

### 1. Edit ThemeService.cs

Open `TansuCloud.Dashboard/Services/ThemeService.cs` and modify the color values:

```csharp
PaletteLight = new PaletteLight
{
    Primary = "#YOUR_BRAND_COLOR",        // Main brand color (buttons, links, AppBar)
    Secondary = "#YOUR_ACCENT_COLOR",     // Secondary actions, chips, badges
    AppbarBackground = "#YOUR_BRAND_COLOR", // Top navigation bar
    // ... other colors
}
```

### 2. Recommended Color Tools

- **Adobe Color**: <https://color.adobe.com/>
- **Material Design Color Tool**: <https://material.io/resources/color/>
- **Coolors**: <https://coolors.co/>
- **Contrast Checker**: <https://webaim.org/resources/contrastchecker/> (ensure WCAG AA compliance)

### 3. Typography Customization

The theme includes Material Design typography scales (H1-H6, Body1-2, Caption, etc.). To adjust:

```csharp
Typography = new Typography
{
    Default = new Default
    {
        FontFamily = new[] { "Your-Font", "Roboto", "sans-serif" },
        FontSize = "0.875rem",
        FontWeight = 400
    },
    H4 = new H4 
    { 
        FontSize = "2.125rem",  // Adjust page heading size
        FontWeight = 400 
    }
    // ... customize other type scales
}
```

**To use a custom font:**

1. Add font link in `Components/App.razor` `<head>`:

   ```html
   <link href="https://fonts.googleapis.com/css2?family=Your+Font:wght@300;400;500;700&display=swap" rel="stylesheet" />
   ```

2. Update `FontFamily` array in `ThemeService.cs`

## MudBlazor Component Gallery

Explore all available components and their variants:

- **Official Docs**: <https://mudblazor.com/components/>
- **Live Examples**: <https://try.mudblazor.com/>

### Key Components Used in TansuCloud

| Component | Usage | Examples |
|-----------|-------|----------|
| `MudDrawer` | Collapsible sidebar | Admin/Tenant navigation |
| `MudAppBar` | Top navigation bar | Both layouts |
| `MudNavMenu` | Hierarchical menu | Sidebar navigation groups |
| `MudCard` | Metric cards, content containers | Dashboard overview |
| `MudDataGrid` | Advanced data tables | API Keys, Users, Policies |
| `MudTable` | Simple tables | Webhooks, logs |
| `MudButton` | Actions | Create, Save, Delete |
| `MudDialog` | Modals | Create/Edit forms |
| `MudSnackbar` | Toast notifications | Success/error messages |
| `MudTextField` | Text inputs | Forms |
| `MudSelect` | Dropdowns | Filters, selectors |

## Layout Dimensions

Configured in `ThemeService.cs` → `LayoutProperties`:

```csharp
LayoutProperties = new LayoutProperties
{
    DrawerWidthLeft = "240px",    // Sidebar width (expanded)
    DrawerWidthRight = "240px",
    AppbarHeight = "64px"         // Top bar height
}
```

**Sidebar behavior:**

- Expanded: 240px (hover-over shows labels)
- Mini: 64px (icons only)
- Automatically collapses on mobile (< 960px breakpoint)

## Dark Mode Support

MudBlazor includes built-in dark mode. To add a toggle:

1. Add dark mode state to your layout:

   ```csharp
   private bool _isDarkMode = false;
   ```

2. Update theme provider:

   ```razor
   <MudThemeProvider Theme="@ThemeService.TansuCloudTheme" IsDarkMode="@_isDarkMode" />
   ```

3. Add toggle button in AppBar:

   ```razor
   <MudIconButton Icon="@(_isDarkMode ? Icons.Material.Filled.LightMode : Icons.Material.Filled.DarkMode)" 
                  Color="Color.Inherit" 
                  OnClick="@(() => _isDarkMode = !_isDarkMode)" />
   ```

## Accessibility (WCAG 2.1 Level AA)

MudBlazor components are designed for accessibility. Ensure:

- **Color Contrast**: Use tools like WebAIM to verify Primary/Secondary colors meet 4.5:1 contrast ratio
- **Focus Indicators**: Built-in (don't disable)
- **Keyboard Navigation**: All interactive elements are keyboard-accessible
- **Screen Readers**: Use `aria-label` props where needed

## Testing Your Theme

After making changes:

1. **Build the project:**

   ```bash
   dotnet build TansuCloud.Dashboard/TansuCloud.Dashboard.csproj
   ```

2. **Run locally:**

   ```bash
   VS Code → Run Task → "dev: up"
   ```

3. **Navigate to pages:**
   - Admin: `http://127.0.0.1:8080/dashboard/admin`
   - Tenant: `http://127.0.0.1:8080/dashboard/tenant/acme-dev`

4. **Check consistency:**
   - All buttons use Primary color
   - Cards use Surface color
   - AppBar matches brand color
   - Navigation icons are visible
   - Text is readable (contrast check)

## Common Customization Scenarios

### Scenario 1: Corporate Blue Theme

```csharp
Primary = "#0056B3",           // Corporate blue
Secondary = "#FFC107",         // Amber accent
AppbarBackground = "#0056B3",
```

### Scenario 2: Nature/Green Theme

```csharp
Primary = "#2E7D32",           // Forest green
Secondary = "#8BC34A",         // Light green accent
AppbarBackground = "#2E7D32",
```

### Scenario 3: High-Contrast for Accessibility

```csharp
Primary = "#000000",           // Black
Secondary = "#FFFFFF",         // White
AppbarBackground = "#000000",
Background = "#FFFFFF",
Surface = "#F0F0F0",
TextPrimary = "#000000",
```

## Need Help?

- **MudBlazor Documentation**: <https://mudblazor.com/>
- **GitHub Issues**: <https://github.com/MudBlazor/MudBlazor/issues>
- **Discord Community**: <https://discord.gg/mudblazor>
- **Material Design Guidelines**: <https://material.io/design>

---

**Pro Tip:** Keep your brand colors in a design system document with Hex codes, RGB values, and usage guidelines. This ensures consistency across all platforms (web, mobile, print).
