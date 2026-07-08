#!/bin/bash
set -e
cd /Users/tarekhosni/Dev/POC/OpenRA
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
export JAVA_HOME="/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home"
BUILD_TOOLS="$HOME/Library/Android/sdk/build-tools/36.1.0"
KEYSTORE="$HOME/.android/debug.keystore"
SERIAL=b01fcbcf

echo "===build APK==="
BUILD_LOG=$(mktemp)
dotnet build OpenRA.Android/OpenRA.Android.csproj -c Release -f net9.0-android -p:BuildForAndroid=true > "$BUILD_LOG" 2>&1
BUILD_RC=$?
grep -E "error|Build succeeded|Build FAILED" "$BUILD_LOG" | tail -5
if [ $BUILD_RC -ne 0 ]; then echo "BUILD FAILED (rc=$BUILD_RC)"; rm -f "$BUILD_LOG"; exit 1; fi
rm -f "$BUILD_LOG"

APK="OpenRA.Android/obj/Release/android/bin/net.openra.android.apk"
echo "===align + sign==="
"$BUILD_TOOLS/zipalign" -f -p 4 "$APK" "${APK%.apk}-aligned.apk"
"$BUILD_TOOLS/apksigner" sign --ks "$KEYSTORE" --ks-pass pass:android --key-pass pass:android \
  --out "${APK%.apk}-Signed.apk" "${APK%.apk}-aligned.apk" 2>/dev/null

echo "===install==="
adb -s $SERIAL install -r "${APK%.apk}-Signed.apk" 2>&1 | tail -2

echo "===launch==="
adb -s $SERIAL logcat -c
ACT=$(adb -s $SERIAL shell dumpsys package net.openra.android 2>/dev/null | grep -oE 'net.openra.android/crc[0-9a-f]+\.MainActivity' | head -1)
adb -s $SERIAL shell am start -n "$ACT" 2>&1 | tail -2
