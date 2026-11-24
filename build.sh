#!/bin/bash
# Builds ArcBox (.NET Framework 4.8, x64) and the NSIS installer into ./dist
set -e
cd "$(dirname "$0")"
VERSION=${VERSION:-1.3.0}
python3 tools/make_icons.py assets/preview.png
dotnet build src/ArcBox/ArcBox.csproj -c Release -f net48 -p:Version=$VERSION
rm -rf dist && mkdir -p dist/app/icons
OUT=src/ArcBox/bin/Release/net48
cp $OUT/ArcBox.exe $OUT/ArcBox.exe.config $OUT/*.dll $OUT/7-Zip-License.txt $OUT/THIRD-PARTY-NOTICES.txt $OUT/COPYING.txt dist/app/
cp $OUT/icons/*.ico dist/app/icons/
makensis -V2 -DVERSION=$VERSION installer/ArcBox.nsi
(cd dist && zip -qr ArcBox-$VERSION-portable.zip app)
ls -la dist
