# Third-party notices

GerGit is GPL-3.0-or-later. It ships with the components below. Licenses of NuGet packages were taken from each package's `.nuspec`.

## NuGet packages

| Package | Version | License |
| --- | --- | --- |
| Avalonia | 12.1.4 | MIT |
| Avalonia.Desktop | 12.1.4 | MIT |
| Avalonia.Themes.Fluent | 12.1.4 | MIT |
| Avalonia.Fonts.Inter | 12.1.4 | MIT (package); the Inter font is OFL-1.1, see below |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| SkiaSharp (transitive, with Linux native assets) | 3.119.4 | MIT |
| HarfBuzzSharp (transitive, with Linux native assets) | 8.3.1.3 | MIT |
| MicroCom.Runtime (transitive) | 0.11.6 | MIT |

Avalonia pulls in further transitive Avalonia packages (for example Avalonia.Skia and Avalonia.X11) under the same license.

The SkiaSharp and HarfBuzzSharp native libraries contain Skia (BSD-3-Clause, Google LLC), HarfBuzz (MIT) and further components; the THIRD-PARTY-NOTICES.txt in those packages lists all of them.

## .NET runtime

The Debian package bundles the .NET runtime (MIT, .NET Foundation and Contributors), copied at build time from Ubuntu's dotnet-sdk-10.0 into the self-contained /usr/lib/gergit/GerGit.

## Font

- **Inter**, copyright 2020 The Inter Project Authors (https://github.com/rsms/inter), SIL Open Font License 1.1, bundled through Avalonia.Fonts.Inter. License text: [packaging/OFL-1.1.txt](packaging/OFL-1.1.txt).

## Icons

- **Lucide** icons (`src/GerGit/Assets/Icons.axaml`, the application icon): ISC license, copyright 2026 Lucide Icons and Contributors (parts: copyright 2013-present Cole Bemis, MIT). See [LUCIDE-LICENSE.txt](src/GerGit/Assets/LUCIDE-LICENSE.txt).
- **Simple Icons** brand icons (`src/GerGit/Assets/BrandIcons.axaml`): CC0 1.0. See [SIMPLE-ICONS-LICENSE.txt](src/GerGit/Assets/SIMPLE-ICONS-LICENSE.txt). Brand logos remain trademarks of their owners.

Test-only packages (xUnit, Microsoft.NET.Test.Sdk, Avalonia.Headless) are not shipped.
