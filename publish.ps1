#/usr/bin/pwsh
dotnet publish ./VkDiag/VkDiag.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=True -p:PublishTrimmed=True -p:PublishAot=False -o ./distrib