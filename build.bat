@echo off
REM WinScreen Build Script

echo Building WinScreen...

REM Clean
dotnet clean -c Release

REM Restore
dotnet restore

REM Build
dotnet build -c Release

REM Publish self-contained executables
echo.
echo Publishing self-contained executables...
dotnet publish src\WinScreen.Server\WinScreen.Server.csproj -c Release -r win-x64 --self-contained -o publish\win-x64
dotnet publish src\WinScreen.Client\WinScreen.Client.csproj -c Release -r win-x64 --self-contained -o publish\win-x64

echo.
echo Build complete! Executables are in publish\win-x64\
echo   - screen.exe (client)
echo   - winscreen-server.exe (server)
echo.
echo Add publish\win-x64 to your PATH to use 'screen' command anywhere.
