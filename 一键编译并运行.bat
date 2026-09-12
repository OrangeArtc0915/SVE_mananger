@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo [1/2] 正在编译星露谷启动器...
dotnet build src\StardewLauncher.App\StardewLauncher.App.csproj -c Debug --nologo
if errorlevel 1 (
    echo.
    echo 编译失败，请查看上面的错误信息。
    pause
    exit /b 1
)

echo [2/2] 正在启动...
start "" "src\StardewLauncher.App\bin\Debug\net8.0-windows\StardewLauncher.exe"
