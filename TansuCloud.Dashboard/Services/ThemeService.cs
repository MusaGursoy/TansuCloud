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
                // Deep Sea Blue Palette
                Primary = "#0466C8", // Bright ocean blue - buttons, links, main actions
                Secondary = "#7D8597", // Cool gray-blue - secondary actions, chips
                Tertiary = "#023E7D", // Deep navy - emphasis, important elements

                // Backgrounds & Surfaces
                AppbarBackground = "#023E7D", // Deep navy top bar (professional)
                Background = "#F8F9FA", // Very light gray page background
                Surface = "#FFFFFF", // White cards and panels
                DrawerBackground = "#001233", // Darkest blue sidebar (creates depth)

                // Text Colors
                TextPrimary = "#001233", // Darkest blue - main text (excellent contrast)
                TextSecondary = "#33415C", // Medium blue-gray - labels, hints
                DrawerText = "#FFFFFF", // White text on dark sidebar
                TextDisabled = "#979DAC", // Light blue-gray - disabled states

                // Action & State Colors
                Success = "#0353A4", // Mid-ocean blue - success states
                Info = "#0466C8", // Bright blue - information
                Warning = "#5C677D", // Muted blue-gray - warnings
                Error = "#002855", // Dark navy - errors (serious)

                // Interactive Elements
                ActionDefault = "#0466C8", // Links and default actions
                ActionDisabled = "#979DAC", // Disabled buttons
                ActionDisabledBackground = "#F8F9FA",

                // Borders & Dividers
                Divider = "#979DAC", // Light blue-gray
                DividerLight = "#979DAC",
                LinesDefault = "#7D8597",
                LinesInputs = "#5C677D",

                // Overlays & Backgrounds
                BackgroundGray = "#F8F9FA",
                OverlayLight = "rgba(1, 35, 51, 0.08)", // #001233 at 8% opacity
                OverlayDark = "rgba(1, 35, 51, 0.15)", // #001233 at 15% opacity
            }
        }; // End of Property TansuCloudTheme
} // End of Class ThemeService