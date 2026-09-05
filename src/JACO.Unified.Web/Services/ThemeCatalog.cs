namespace JACO.Unified.Web.Services;

public sealed record ThemeOption(string Key, string Name, string Pitch, string[] Swatches);

// The visual templates a user can pick for JAMS specifically -- independent of the "Theme"
// claim Portal writes for the rest of the JACO platform (orange/fiori only, shared via SSO
// across Portal/CR/Approval/Sales Discount). A choice made here is stored in a Unified-only
// cookie (see ThemesController) and takes precedence over that shared claim once set, so
// picking a template in JAMS never touches Portal or the other apps' own look. "orange" and
// "fiori" stay selectable through this same gallery for a consistent single picker, they
// just resolve to the existing jaco-design-system(.fiori).css pair rather than a new file.
public static class ThemeCatalog
{
    public static readonly ThemeOption[] Options =
    [
        new("orange", "Classic Orange", "The original JAMS look — navy sidebar, JACO orange accent.", ["#0a0e16", "#f2600c", "#15803d", "#1d4ed8"]),
        new("fiori", "Fiori", "SAP Fiori-style: flat, blue-accented, familiar to SAP users.", ["#1B2A4A", "#0A6ED1", "#107E3E", "#E9730C"]),
        new("boardroom", "Boardroom", "A dark board with the colour turned up — solid status pills, outlined tags, avatars. monday.com's own move.", ["#1c1d23", "#ff5a3c", "#00c875", "#4a90e2"]),
        new("showroom", "Showroom", "JACO's own showroom floor — navy, brass and the house orange, built for gravity, not gimmicks.", ["#0b0f1a", "#f2600c", "#c9a05b", "#4fd48a"]),
        new("nightshift", "Night Shift", "A diagnostic bay after hours — amber-on-black, monospaced IDs, for whoever lives in this screen all day.", ["#0a0c0a", "#ffb000", "#29d3c6", "#7fe0c4"]),
        new("daylight", "Daylight", "One rule, kept precisely: no boxes, no shadows. The number is the design.", ["#ffffff", "#1e4fff", "#16171a", "#dedfe3"]),
        new("meadow", "Meadow", "Soft edges, quiet colour — for the requester who opens this once a month and shouldn't have to think.", ["#f4f6f1", "#4c8577", "#e8785a", "#6f8fae"]),
        new("flux", "Flux", "Frosted glass over a fluid gradient ground — cards lift and glow on hover, Stripe/Linear-coded.", ["#0d0f1a", "#7c6cff", "#4f8bff", "#34e5b0"]),
        new("collab", "Collab", "A light, airy shell with one confident indigo accent — every avatar gets a soft presence ring.", ["#6366f1", "#f6f7fb", "#16a34a", "#dc2626"]),
        new("prism", "Prism", "The most colour of the set — solid saturated badges and fully rounded shapes throughout.", ["#7b5cff", "#ff3d57", "#00c48c", "#3d5aff"]),
    ];

    public static bool IsValid(string? key) => key is not null && Options.Any(o => o.Key == key);
    public static ThemeOption Get(string key) => Options.FirstOrDefault(o => o.Key == key) ?? Options[0];
}
