#!/usr/bin/env bash
cd $(dirname $0)
export BUILD_MODE=Release
mono nuget.exe restore ./TerrariaServerAPI/
xbuild ./TerrariaServerAPI/TShock.4.OTAPI.sln /p:Configuration=$BUILD_MODE
cd ./TerrariaServerAPI/TShock.Modifications.Bootstrapper/bin/$BUILD_MODE/
mono TShock.Modifications.Bootstrapper.exe -in=OTAPI.dll \
    -mod=../../../TShock.Modifications.**/bin/$BUILD_MODE/TShock.Modifications.*.dll \
    -o=Output/OTAPI.dll
cd ../../../../
xbuild ./TerrariaServerAPI/TerrariaServerAPI/TerrariaServerAPI.csproj \
    /p:Configuration=$BUILD_MODE
mono nuget.exe restore
xbuild ./TShock.sln /p:Configuration=$BUILD_MODE
