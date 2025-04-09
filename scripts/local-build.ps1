# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

. (Join-Path $PSScriptRoot "version.ps1")
$version = $versionString.Substring(1)
$platform = "linux-arm64"

Remove-Item ../bld -Recurse -Force

dotnet publish ../dev-proxy/dev-proxy.csproj -c Release -p:PublishSingleFile=true -r $platform --self-contained -o ../bld -p:InformationalVersion=$version
dotnet build ../dev-proxy-plugins/dev-proxy-plugins.csproj -c Release -r $platform --no-self-contained -p:InformationalVersion=$version
cp -R ../dev-proxy/bin/Release/net9.0/$platform/plugins ../bld
pushd

cd ../bld
Get-ChildItem -Filter *.pdb -Recurse | Remove-Item
Get-ChildItem -Filter *.deps.json -Recurse | Remove-Item
Get-ChildItem -Filter *.runtimeconfig.json -Recurse | Remove-Item
Get-ChildItem -Filter *.staticwebassets.endpoints.json -Recurse | Remove-Item
Get-ChildItem -Filter web.config -Recurse | Remove-Item
popd