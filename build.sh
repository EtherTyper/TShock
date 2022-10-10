#!/usr/local/bin/bash
cd "$(dirname $0)"/TShockLauncher
dotnet publish -f net6.0 -c Release
