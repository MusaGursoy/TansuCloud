// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using MudBlazor;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Centralized theme service for consistent branding across all Dashboard layouts.
/// </summary>
public class ThemeService
{
    public MudTheme TansuCloudTheme { get; } =
        new()
        {
            PaletteLight = new PaletteLight
            {
                // TansuCloud Brand Colors (Teal/Navy Palette)
                Primary = "#014F86",           // TansuCloud brand teal-blue
                Secondary = "#2A6F97",         // Secondary teal
                Tertiary = "#61A5C2",          // Light blue accent
                
                // Backgrounds & Surfaces
                AppbarBackground = "#012A4A",  // Deep navy top bar
                Background = "#F5F5F5",        // Light gray page background
                Surface = "#FFFFFF",           // White card/paper surfaces
                DrawerBackground = "#013A63",  // Dark teal sidebar
                
                // Text Colors
                TextPrimary = "#000000",       // Black text
                TextSecondary = "rgba(0,0,0,0.6)", // Gray text
                DrawerText = "#E0F4FF",        // Very light blue text on dark sidebar (higher contrast)
                DrawerIcon = "#C5E8FF",        // Light blue icons (higher contrast)
                TextDisabled = "rgba(0,0,0,0.38)",
                
                // Action & State Colors (Material Design defaults work well)
                Success = "#4CAF50",
                Info = "#2196F3",
                Warning = "#FF9800",
                Error = "#F44336",
                
                // Interactive Elements
                ActionDefault = "#014F86",     // Primary brand color
                ActionDisabled = "rgba(0,0,0,0.26)",
                ActionDisabledBackground = "rgba(0,0,0,0.12)",
                
                // Borders & Dividers
                Divider = "rgba(0,0,0,0.12)",
                DividerLight = "rgba(0,0,0,0.06)",
                LinesDefault = "rgba(0,0,0,0.12)",
                LinesInputs = "rgba(0,0,0,0.42)",
                
                // Overlays & Backgrounds
                BackgroundGray = "#F5F5F5",
                OverlayLight = "rgba(255,255,255,0.5)",
                OverlayDark = "rgba(33,33,33,0.48)"
            },
            PaletteDark = new PaletteDark
            {
                // TansuCloud Brand Colors (Dark Mode)
                Primary = "#61A5C2",           // Light blue for dark mode
                Secondary = "#89C2D9",         // Lighter blue accent
                Tertiary = "#A9D6E5",          // Lightest blue
                
                // Backgrounds & Surfaces
                AppbarBackground = "#012A4A",  // Deep navy
                Background = "#01497C",        // Dark teal background
                Surface = "#013A63",           // Dark teal surfaces
                DrawerBackground = "#012A4A",  // Deep navy sidebar
                
                // Text Colors
                TextPrimary = "#FFFFFF",       // White text
                TextSecondary = "rgba(255,255,255,0.7)",
                DrawerText = "#E0F4FF",        // Very light blue text (higher contrast)
                DrawerIcon = "#C5E8FF",        // Light blue icons (higher contrast)
                TextDisabled = "rgba(255,255,255,0.5)",
                
                // Action & State Colors
                Success = "#66BB6A",
                Info = "#42A5F5",
                Warning = "#FFA726",
                Error = "#EF5350",
                
                // Interactive Elements
                ActionDefault = "#61A5C2",
                ActionDisabled = "rgba(255,255,255,0.3)",
                ActionDisabledBackground = "rgba(255,255,255,0.12)",
                
                // Borders & Dividers
                Divider = "rgba(255,255,255,0.12)",
                DividerLight = "rgba(255,255,255,0.06)",
                LinesDefault = "rgba(255,255,255,0.12)",
                LinesInputs = "rgba(255,255,255,0.7)",
                
                // Overlays & Backgrounds
                BackgroundGray = "#01497C",
                OverlayLight = "rgba(255,255,255,0.05)",
                OverlayDark = "rgba(0,0,0,0.5)"
            }
        }; // End of Property TansuCloudTheme
} // End of Class ThemeService