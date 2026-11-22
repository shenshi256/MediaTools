@echo off
setlocal

echo ============================================
echo   MediaTools 提交并推送 (GitHub + Gitee)
echo ============================================
echo.

set "ROOT=%~dp0"
cd /d "%ROOT%"

echo [提示] 当前分支: 
git branch --show-current
echo.

echo [提示] 当前改动:
git status --short
echo.

set /p MSG=请输入提交信息 (commit message): 

if "%MSG%"=="" (
  echo [错误] 提交信息不能为空，已取消。
  pause
  exit /b 1
)

echo.
echo [提示] 正在 git add -A ...
git add -A

echo [提示] 正在提交: %MSG%
git commit -m "%MSG%"

if errorlevel 1 (
  echo [错误] 提交失败（可能没有可提交的改动），请检查上方报错。
  pause
  exit /b 1
)

echo.
echo [提示] 推送到 GitHub (origin/main)...
git push origin main

echo.
echo [提示] 推送到 Gitee (gitee/master)...
git push gitee main:master

echo.
echo ============================================
echo  完成!
echo  GitHub: origin/main
echo  Gitee : gitee/master
echo ============================================
echo.
pause
