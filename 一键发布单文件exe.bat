@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo 正在发布为单文件 exe，首次执行需要下载运行时，请耐心等待...
dotnet publish src\StardewLauncher.App\StardewLauncher.App.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -o "publish" ^
    --nologo

if errorlevel 1 (
    echo.
    echo 发布失败，请查看上面的错误信息。
    pause
    exit /b 1
)

echo.
echo 发布完成：%cd%\publish\StardewLauncher.exe
echo 该 exe 已自带运行时，拷到任何 Windows 电脑上双击即可运行。
pause
