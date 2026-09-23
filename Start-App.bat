@echo off
setlocal
if not exist "%~dp0..\KnowledgeApp-builds\Start-App.bat" (
  echo KnowledgeApp launcher was not found in the adjacent KnowledgeApp-builds folder.
  pause
  exit /b 1
)
call "%~dp0..\KnowledgeApp-builds\Start-App.bat"
exit /b %errorlevel%
