# Step-by-Step Build Instructions for Fyle

## Prerequisites

### Step 1: Install .NET 8 SDK

1. Go to: https://dotnet.microsoft.com/download/dotnet/8.0
2. Download the **.NET 8 SDK** (not just the Runtime)
3. Run the installer and follow the prompts
4. Restart your terminal/PowerShell after installation

### Step 2: Verify Installation

Open PowerShell and run:
```powershell
dotnet --version
```

You should see something like `8.0.xxx`. If you get an error, restart your computer and try again.

---

## Building the Application

### Step 3: Navigate to Project Directory

Open PowerShell and navigate to the project folder:

```powershell
cd C:\Fyle-App\Fyle
```

**Note:** If you're already in the project folder (you can see `Fyle.csproj` in the current directory), you can skip this step.

### Step 4: Restore Dependencies

First, restore the NuGet packages:

```powershell
dotnet restore
```

This downloads any required packages (should be quick, we only use System.Management).

### Step 5: Build the Project

Build the project in Release mode:

```powershell
dotnet build -c Release
```

This compiles the code. You should see "Build succeeded" at the end.

### Step 6: Publish as Single Executable

Create a single portable .exe file:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

This will take a minute or two. It creates a self-contained executable that doesn't require .NET to be installed on other computers.

---

## Running the Application

### Step 7: Find the Executable

After publishing, the executable will be located at:

```
C:\Fyle-App\Fyle\bin\Release\net8.0-windows\win-x64\publish\Fyle.exe
```

### Step 8: Run the Application

**Option A: From PowerShell**
```powershell
.\bin\Release\net8.0-windows\win-x64\publish\Fyle.exe
```

**Option B: From File Explorer**
1. Navigate to: `C:\Fyle-App\Fyle\bin\Release\net8.0-windows\win-x64\publish\`
2. Double-click `Fyle.exe`

**Option C: Copy to Desktop**
You can copy `Fyle.exe` to your Desktop or anywhere else - it's completely portable!

---

## Quick Reference (All Commands in One)

If you're already in the project directory:

```powershell
# Restore packages
dotnet restore

# Build
dotnet build -c Release

# Publish as single .exe
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Run it
.\bin\Release\net8.0-windows\win-x64\publish\Fyle.exe
```

---

## Troubleshooting

**"dotnet command not found"**
- Install .NET 8 SDK (Step 1)
- Restart your terminal/PowerShell
- Restart your computer if needed

**"Build failed"**
- Make sure you're in the correct directory (should contain `Fyle.csproj`)
- Run `dotnet restore` first
- Check for error messages in the output

**"Access denied" errors**
- Make sure no antivirus is blocking the build
- Try running PowerShell as Administrator

**The .exe is large (~50-100MB)**
- This is normal! It includes the entire .NET runtime
- The benefit is it works on any Windows 10/11 PC without installing .NET

---

## Next Steps

Once built, you can:
- Copy `Fyle.exe` to any Windows computer and run it
- No installation needed
- No internet required
- Share it with others!

