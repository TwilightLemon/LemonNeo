# Desktop theme colors

In **Settings → Appearance**, choose light, dark, or system brightness independently
of the built-in color palette. The nine palettes are Classic green, Polar rose,
Basalt snow, Moonlit gold, Aurora sea, Crystal blue, Polar night, Glacial sea, and
Misty twilight. Each adapts the supplied reference colors to restrained background
tints and readable UI accents.

Enable **Follow the playing track's artwork** to derive colors from the current
cover. Missing, failed, transparent, or unreadable artwork falls back to the selected
built-in palette. Choosing another built-in palette while artwork colors are active
updates the fallback. Disabling artwork colors immediately restores that palette.
The two choices are saved in `desktop.ui` as `themeColor` and `useArtworkColors`;
older settings retain the classic palette and leave artwork colors disabled.

Colors transition together over 420 ms. Rapid changes continue from the visible
frame. **Reduce animations** completes transitions immediately, including a
transition already in progress. Startup restores preferences without animating.

## Implementation

- `ThemePalette` owns the reference colors and builds semantic colors for each
  brightness mode. Primary text targets 7:1 contrast; secondary text and accent
  colors have extra contrast headroom for intermediate frames and hover states.
  Tests require at least 4.5:1 for normal text and button labels.
- `ThemeResources` replaces the old fixed XAML palette with shared, mutable
  `Brush.*` resources. Views and templates continue using `DynamicResource`.
- `ThemeColorService` combines brightness, fallback, and cached artwork color.
  It listens for effective application theme changes and updates brushes and
  Fluent accents on one UI dispatcher timer. Its lifetime ends at app shutdown.
- `ArtworkColorExtractor` follows LemonLite's `ImageHelper.GetMajorColor`: sample
  decoded pixels at a stride of `max(1, min(width, height) / 100)`, group RGB values
  in sixteen-value bins, and exclude mean channel intensities below 24 or above
  230. Bins are ranked by `population × saturation × (1 - abs(lightness - 0.6) × 1.8)`.
  Direct sampling preserves colors that an extra resize would blend. Avalonia
  converts pixels to unpremultiplied BGRA; alpha weights the population and pixels
  with alpha below 128 are ignored. Fully transparent samples return no color;
  visible artwork with only extreme black/white samples uses neutral gray. Equal
  saturation scores favor the largest region, including on grayscale covers.
  Saturation and brightness are adapted before colors reach the UI.
- `PlaybackViewModel` reuses the existing cover request and extracts its color
  off the UI thread. Cancellation and URI checks prevent late requests from
  changing a newer track's cover or theme. There is no second artwork download.

Add new palettes to `ThemePalette.BuiltIn`, append their enum values to preserve
saved numeric IDs, and add their names to both language resource files. Color
literals belong in palette definitions rather than views.

Fluent's supported dynamic palette input is `Accent`; see the
[Avalonia theme documentation](https://docs.avaloniaui.net/docs/styling/themes).

## Verification

```sh
dotnet test tests/LemonNeo.Desktop.Tests/LemonNeo.Desktop.Tests.csproj -c Release -p:SkipDesktopPlugins=true
```

The suite verifies both brightness modes, extreme and random artwork colors,
intermediate built-in palette colors, alpha conversion, monochrome/transparent
covers, preference persistence and legacy defaults, failed saves, animated
retargeting, reduced motion, actual settings controls, and delayed/invalid cover
requests. A rendered lyric test also checks that a paused highlight updates when
its shared theme brush changes. Tests use temporary configuration and library
directories.

To save rendered settings previews for all eighteen palette/mode combinations:

```powershell
$env:LEMONNEO_THEME_PREVIEW_DIR = Join-Path (Get-Location) 'artifacts/theme-preview'
dotnet test tests/LemonNeo.Desktop.Tests/LemonNeo.Desktop.Tests.csproj -c Release -p:SkipDesktopPlugins=true --filter FullyQualifiedName~SettingsViewTests
```
