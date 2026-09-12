@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "OUTDIR=publish"
rem 必须用绝对路径：相对路径会让 MSBuild 在每个项目目录下再写一份中间产物
set "OUTABS=%~dp0publish"
set "APPINFO=src\StardewLauncher.Core\App\AppInfo.cs"
set "CSPROJ=src\StardewLauncher.App\StardewLauncher.App.csproj"

echo ============================================
echo   星露谷启动器 - 发行构建
echo ============================================
echo.

rem ---------- 读取版本号（唯一来源：AppInfo.cs）----------
powershell -NoProfile -Command "$m=[regex]::Match((Get-Content -Raw '%APPINFO%'), 'public const string Version = .([0-9]+[.][0-9]+[.][0-9]+).'); if($m.Success){$m.Groups[1].Value}" > "_version.tmp"
set /p VERSION=<"_version.tmp"
del /f /q "_version.tmp" 2>nul

if not defined VERSION (
    echo [错误] 没能从 %APPINFO% 里读到版本号，请检查该文件是否被改动过。
    goto :failed
)
set "ZIPNAME=StardewLauncher-v%VERSION%-win-x64.zip"
echo 版本号：%VERSION%
echo.

rem ---------- 1/4 清理上次产物 ----------
rem 只删 exe 与 zip；%OUTDIR%\Data 是运行时数据（设置、实例、日志、API Key），必须保留
echo [1/4] 清理上次产物...
if exist "%OUTDIR%\StardewLauncher.exe" del /f /q "%OUTDIR%\StardewLauncher.exe"
if exist "%OUTDIR%\*.zip" del /f /q "%OUTDIR%\*.zip"
if exist "%OUTDIR%\_pkg" rmdir /s /q "%OUTDIR%\_pkg"

rem ---------- 2/4 发布单文件 exe ----------
echo [2/4] 发布单文件 exe（首次执行需下载运行时，请耐心等待）...
dotnet publish "%CSPROJ%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:Version=%VERSION% ^
    -o "%OUTABS%" ^
    --nologo
if errorlevel 1 goto :failed

if not exist "%OUTDIR%\StardewLauncher.exe" (
    echo [错误] 没有生成 %OUTDIR%\StardewLauncher.exe
    goto :failed
)

rem dotnet publish 会顺带在各项目目录下再写一份中间产物，清掉以免留下上百 MB 垃圾
call :cleanIntermediate

rem ---------- 3/4 组装发行包 ----------
echo.
echo [3/4] 组装发行包...
set "PKG=%OUTDIR%\_pkg"
mkdir "%PKG%" 2>nul
copy /y "%OUTDIR%\StardewLauncher.exe" "%PKG%\StardewLauncher.exe" >nul
if exist "README.md" copy /y "README.md" "%PKG%\README.md" >nul
if exist "NOTICE"    copy /y "NOTICE"    "%PKG%\NOTICE"    >nul
if exist "LICENSE"   copy /y "LICENSE"   "%PKG%\LICENSE"   >nul

powershell -NoProfile -Command "Compress-Archive -Path '%PKG%\*' -DestinationPath '%OUTDIR%\%ZIPNAME%' -Force"
if errorlevel 1 goto :failed
rmdir /s /q "%PKG%"

rem ---------- 4/4 完成 ----------
echo.
echo [4/4] 完成。
echo.
echo   单文件 exe : %cd%\%OUTDIR%\StardewLauncher.exe
echo   发行压缩包 : %cd%\%OUTDIR%\%ZIPNAME%
echo.
echo 提示：exe 已自带 .NET 8 运行时，拷到任意 Windows 10 1809+ / 11 x64 上双击即可运行。
echo       %OUTDIR%\Data 是运行时数据（设置、实例、日志），本次构建未做改动。
echo.
pause
exit /b 0

:failed
echo.
echo 构建失败，请查看上面的错误信息。
if exist "%OUTDIR%\_pkg" rmdir /s /q "%OUTDIR%\_pkg"
call :cleanIntermediate
pause
exit /b 1

:cleanIntermediate
if exist "src\StardewLauncher.App\publish" rmdir /s /q "src\StardewLauncher.App\publish"
if exist "src\StardewLauncher.Core\publish" rmdir /s /q "src\StardewLauncher.Core\publish"
exit /b 0
