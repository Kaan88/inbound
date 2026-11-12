@echo off
rem Builds the native torrent worker for win-x64 (static CRT, static OpenSSL) and copies it to
rem artifacts\torrent-worker\win-x64. INBOUND_VCPKG_ROOT points at a vcpkg checkout (default: idm-deps\vcpkg next to
rem the repository); the manifest pins the baseline. VCPKG_ROOT is not used, because vcvars sets it to Visual Studio's copy.
setlocal
set "HERE=%~dp0"
set "REPO=%HERE%..\.."
set "VCPKG=%INBOUND_VCPKG_ROOT%"
if "%VCPKG%"=="" set "VCPKG=%REPO%\..\idm-deps\vcpkg"
if not exist "%VCPKG%\vcpkg.exe" (echo vcpkg not found at %VCPKG% & exit /b 1)
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if "%VSDIR%"=="" (echo Visual Studio with the C++ tools not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set "BUILD=%REPO%\artifacts\torrent-worker\build-win-x64"
set "INSTALLED=%VCPKG%\..\installed"
cmake -S "%HERE%." -B "%BUILD%" -G Ninja -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_TOOLCHAIN_FILE="%VCPKG%\scripts\buildsystems\vcpkg.cmake" ^
  -DVCPKG_TARGET_TRIPLET=x64-windows-static-rel -DVCPKG_OVERLAY_TRIPLETS="%HERE%triplets" ^
  -DVCPKG_INSTALLED_DIR="%INSTALLED%" || exit /b 1
cmake --build "%BUILD%" || exit /b 1
"%BUILD%\inbound-torrent-selftest.exe" || exit /b 1
if not exist "%REPO%\artifacts\torrent-worker\win-x64" mkdir "%REPO%\artifacts\torrent-worker\win-x64"
copy /y "%BUILD%\inbound-torrent.exe" "%REPO%\artifacts\torrent-worker\win-x64\inbound-torrent.exe" >nul || exit /b 1
"%REPO%\artifacts\torrent-worker\win-x64\inbound-torrent.exe" --version
