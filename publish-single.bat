@echo off
setlocal

echo ============================================
echo   MediaDown 单文件发布
echo ============================================
echo.

set "ROOT=%~dp0"
set "OUT=%ROOT%publish-single"

echo [提示] 输出目录: %OUT%
echo [提示] 正在清理旧输出...
if exist "%OUT%" rmdir /s /q "%OUT%"

echo [提示] 正在 dotnet publish (单文件, 自包含)...
dotnet publish "%ROOT%MediaDownloader.App\MediaDownloader.App.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "%OUT%"

if errorlevel 1 (
  echo [错误] publish 失败，请检查上方报错。
  pause
  exit /b 1
)

echo.
echo [提示] 拷贝随包工具 (yt-dlp / node / ffmpeg)...
if exist "%ROOT%tools\yt-dlp"   xcopy /e /i /q "%ROOT%tools\yt-dlp"   "%OUT%\tools\yt-dlp"   >nul
if exist "%ROOT%tools\node"     xcopy /e /i /q "%ROOT%tools\node"     "%OUT%\tools\node"     >nul
if exist "%ROOT%tools\ffmpeg"   xcopy /e /i /q "%ROOT%tools\ffmpeg"   "%OUT%\tools\ffmpeg"   >nul

echo [提示] 清理调试符号和文档注释 (pdb / xml)...
del /q "%OUT%\*.pdb"  2>nul
del /q "%OUT%\*.xml"  2>nul

echo [提示] 重命名主程序为 MediaTools.exe...
if exist "%OUT%\MediaDownloader.App.exe" (
  ren "%OUT%\MediaDownloader.App.exe" MediaTools.exe
)

echo.
echo ============================================
echo  发布完成!
echo  目录: %OUT%
echo  其中 MediaTools.exe 是单文件主程序
echo  (自带 .NET 运行时, 目标机器无需安装)
echo  tools 目录是下载引擎, 必须和 exe 放一起
echo ============================================
echo.
pause
