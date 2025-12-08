# Fyle - Disk Usage Analyzer

A lightweight, offline desktop application for Windows that visualizes disk usage with a modern treemap interface. Better than SpaceSniffer, with a clean, professional UI.

## Features

- **Drive Selection**: Scan any available drive (C:, D:, E:, etc.)
- **Multi-threaded Scanning**: Fast recursive directory scanning
- **Modern Treemap Visualization**: Clean, rounded rectangles with smooth interactions
- **Light & Dark Themes**: Toggle between themes with one click
- **Navigation**: Click to zoom into folders, back button to navigate up
- **Progress Tracking**: Real-time scanning progress with current path
- **No Internet Required**: Completely offline, no telemetry or analytics
- **Single Executable**: Portable .exe file, no installation needed

## Building

### Requirements
- .NET 8 SDK
- Windows 10/11

### Build Steps

1. Open a terminal in the project directory
2. Run:
   ```bash
   dotnet build -c Release
   ```
3. Publish as single executable:
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
   ```
4. The executable will be in `bin/Release/net8.0-windows/win-x64/publish/Fyle.exe`

## Usage

1. Run `Fyle.exe`
2. Select a drive from the sidebar
3. Wait for the scan to complete
4. Click on any folder in the treemap to zoom in
5. Use the Back button to navigate up
6. Toggle theme with the 🌓 button

## Admin Privileges

The app will prompt for administrator privileges when scanning system drives (C: drive, Windows folders, Program Files). This is required to access protected directories.

## Performance

- Handles large drives (1-4 TB)
- Low memory usage (<300MB)
- Responsive UI during scanning
- Multi-threaded for optimal speed

## License

This project is provided as-is for personal use.

