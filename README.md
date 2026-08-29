# DesktopIniManager

DesktopIniManager is a Windows developer tool for exploring repository structures and organizing folders with custom icons through `desktop.ini`.

## Features

- Find Git repositories without manually entering `.git`.
- Browse results as a physical folder tree.
- Parse Visual Studio `.sln` files and display their logical solution structure.
- Detect projects inside monorepos and large development folders.
- Analyze source-language composition, including C#, C/C++, JavaScript, TypeScript, PHP, Java, Delphi, VB6, VB.NET, and many others.
- Search ordinary folders by multiple extensions such as `mp3 wav` and display counts for every match.
- Preview and select icons from ICO, ICL, DLL, and EXE resources.
- Apply or remove folder icon settings in batches.
- Optionally add `desktop.ini` to `.gitignore`.
- Switch between light and dark themes.
- Reuse a result folder as a narrower search location from its context menu.

## Requirements

- Windows
- .NET Framework 4.8
- Visual Studio 2022 or newer with the WPF desktop workload

## Build

Run the Visual Studio MSBuild executable from a Developer PowerShell:

```powershell
MSBuild.exe DesktopIniManager.sln /t:Build /p:Configuration=Release
```

The application is written in WPF and targets x64.

## How folder icons are applied

DesktopIniManager writes a `[.ShellClassInfo]` section containing the selected `IconResource`, marks `desktop.ini` as Hidden and System, applies the System attribute to the folder, and notifies Windows Explorer of the change.

The icon library remains at the path selected by the user. Moving or deleting that file can make assigned folder icons unavailable.
