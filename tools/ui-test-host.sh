#!/usr/bin/env bash
# An isolated application and database set; uses the local development SQL/Keycloak services.
set -euo pipefail
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__ControlPlane='Server=localhost,1433;Database=betsi_browser_control;User Id=sa;Password=Betsi_Dev_Password1;TrustServerCertificate=True;Encrypt=False'
export Tenancy__SeedTenants__0__DatabaseName=betsi_browser_glan_clwyd
export Tenancy__SeedTenants__1__DatabaseName=betsi_browser_wrexham
export Serilog__MinimumLevel__Default=Warning
export Authentication__Interactive__SessionLifetime=00:02:00
cd Betsi
exec dotnet bin/Debug/net10.0/Betsi.Core.dll --urls http://localhost:8080
