<#
.SYNOPSIS
  Removes the add-in's COM and SolidWorks registration. Run from an elevated PowerShell with SolidWorks closed.
  Chats already saved inside your SolidWorks files are left untouched.
#>
param([ValidateSet("Debug", "Release")][string]$Configuration = "Debug")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root "src\SwAgent\bin\$Configuration\net48\SwAgent.dll"
$regasm = Join-Path $env:windir "Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
& $regasm $dll /unregister /nologo
Write-Host "Unregistered. Restart SolidWorks." -ForegroundColor Green
