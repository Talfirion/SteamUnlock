@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

:: Проверка прав администратора
net session >nul 2>&1 || (
    echo [!] ОШИБКА: Запустите от имени АДМИНИСТРАТОРА!
    pause
    exit /b
)

:: Переходим в папку bin, где лежат все файлы
cd /d "%~dp0bin"

echo [*] Проверка файлов в папке: %cd%
set "missing=0"
for %%f in (winws.exe WinDivert.dll WinDivert64.sys ..\list.txt) do (
    if not exist "%%f" (
        echo [!] ФАЙЛ НЕ НАЙДЕН: %%f
        set "missing=1"
    ) else (
        echo [+] Найдено: %%f
    )
)

if "!missing!"=="1" (
    echo.
    echo [!!!] ОШИБКА: Убедитесь, что файлы лежат в папке bin, а list.txt — рядом с батником.
    pause
    exit /b
)

echo.
echo [*] Очистка DNS...
ipconfig /flushdns >nul

echo [*] Запуск... (Параметры: split2, hostlist=list.txt)
echo.

:: Запуск. Обратите внимание: list.txt берется из папки уровнем выше (..\list.txt)
winws.exe --wf-tcp=80,443 --wf-udp=443,50000-65535 --hostlist="..\list.txt" --dpi-desync=split2 --dpi-desync-split-pos=2 --dpi-desync-repeats=6

if %errorlevel% neq 0 (
    echo.
    echo [!!!] Программа вылетела с ошибкой #%errorlevel%
)
pause