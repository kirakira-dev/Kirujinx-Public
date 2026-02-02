#!/bin/bash

set -e

PUBLISH_DIRECTORY=$1
OUTPUT_DIRECTORY=$2
ENTITLEMENTS_FILE_PATH=$3

APP_BUNDLE_DIRECTORY="$OUTPUT_DIRECTORY/Kirujinx.app"

rm -rf "$APP_BUNDLE_DIRECTORY"
mkdir -p "$APP_BUNDLE_DIRECTORY/Contents"
mkdir "$APP_BUNDLE_DIRECTORY/Contents/Frameworks"
mkdir "$APP_BUNDLE_DIRECTORY/Contents/MacOS"
mkdir "$APP_BUNDLE_DIRECTORY/Contents/Resources"

# Copy executable and ensure executable can be executed
cp "$PUBLISH_DIRECTORY/Ryujinx" "$APP_BUNDLE_DIRECTORY/Contents/MacOS/Kirujinx"
chmod u+x "$APP_BUNDLE_DIRECTORY/Contents/MacOS/Kirujinx"

# Then all libraries
cp "$PUBLISH_DIRECTORY"/*.dylib "$APP_BUNDLE_DIRECTORY/Contents/Frameworks"

# Then resources
cp Info.plist "$APP_BUNDLE_DIRECTORY/Contents"
cp Kirujinx.icns "$APP_BUNDLE_DIRECTORY/Contents/Resources/Kirujinx.icns"
cp updater.sh "$APP_BUNDLE_DIRECTORY/Contents/Resources/updater.sh"
cp Assets.car "$APP_BUNDLE_DIRECTORY/Contents/Resources/Assets.car"
cp -r "$PUBLISH_DIRECTORY/THIRDPARTY.md" "$APP_BUNDLE_DIRECTORY/Contents/Resources"

echo -n "APPL????" > "$APP_BUNDLE_DIRECTORY/Contents/PkgInfo"

# Fixup libraries and executable
echo "Running bundle fix up python script"
python3 bundle_fix_up.py "$APP_BUNDLE_DIRECTORY" MacOS/Kirujinx

# Now sign it
echo "Starting signing process"
if ! [ -x "$(command -v codesign)" ];
then
    if ! [ -x "$(command -v rcodesign)" ];
    then
        echo "Cannot find rcodesign on your system, please install rcodesign and ensure it is in your search path."
        exit 1
    fi

    echo "Using rcodesign for ad-hoc signing"

    echo "Resigning all frameworks dylib files as ad-hoc"
    find "$APP_BUNDLE_DIRECTORY/Contents/Frameworks" -type f -name "*.dylib" -exec rcodesign sign {} \;

    echo "Signing app bundle as ad-hoc"
    rcodesign sign --entitlements-xml-path "$ENTITLEMENTS_FILE_PATH" "$APP_BUNDLE_DIRECTORY"
else
    echo "Using codesign for ad-hoc signing"

    echo "Resigning all frameworks dylib files as ad-hoc"
    find "$APP_BUNDLE_DIRECTORY/Contents/Frameworks" -type f -name "*.dylib" -exec codesign --force --sign - {} \;

    echo "Signing app bundle as ad-hoc"
    codesign --entitlements "$ENTITLEMENTS_FILE_PATH" -f -s - "$APP_BUNDLE_DIRECTORY"
fi
