# Startup splash

The borderless splash uses `Assets/splash-background.png`, embedded in the
application executable. Copyright and startup status are live WPF text, not
part of the bitmap. The copyright line reads `© <year> ZEBRASOFT Co.,Ltd.` with
`By Tamayan` beneath it.

Startup loads settings, restores the saved Physical/Solution trees and their
icons, and prepares the selected icon-library preview in the background.
The prepared data is handed to the main window without reading it again.
The four progress steps represent preparation and workspace construction;
they are not estimates of elapsed time. No artificial delay is added. The
splash closes after the main window has rendered. Startup does not trigger
a new drive scan unless explicitly requested through the existing command-line
search options.

Run the regression harness with `--startup <absolute-path-to-App.xaml>` to
check preparation, tree handoff, image rendering, and corrupt-cache recovery.
The test writes a preview into its isolated startup fixture directory.

## Background asset provenance

Source: user-provided `ChatGPT Image 2026年9月3日 11_22_42.png`.
Edited with the built-in image-generation tool, preserving the original source.
Final asset: `Assets/splash-background.png`.

Prompt:

> Edit the supplied DesktopIniManager splash image as a software background asset.
> Preserve the exact wide 2:1 composition, logo, DesktopIniManager title, subtitle,
> right-hand project tree illustration, blue diagonal highlights, and bottom DIM
> tagline. Remove ONLY the four baked-in startup-status lines in the lower
> middle-left: 'Initializing...', 'Scanning drives...', 'Loading project
> information...', and 'Analyzing solution structure...'. Seamlessly reconstruct
> their dark navy background so that entire region stays empty for live application
> status text. Keep the thin horizontal separator above that region. Do not add
> copyright text or any new text; copyright and status will be rendered live by
> the app. Keep image dimensions/aspect ratio as close to the original 1774x887
> as possible. This is a precise cleanup, not a redesign.
