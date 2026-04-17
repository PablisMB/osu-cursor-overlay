@echo off
echo Restaurando cursor del sistema...
powershell -NoProfile -Command "Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class C { [DllImport(\"user32.dll\")] public static extern bool SystemParametersInfo(uint a,uint b,IntPtr c,uint d); }'; [C]::SystemParametersInfo(0x57,0,[IntPtr]::Zero,0)" >nul 2>&1
echo Cursor restaurado. Puedes cerrar esta ventana.
timeout /t 2 >nul
