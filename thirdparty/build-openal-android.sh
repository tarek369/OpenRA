#!/bin/bash
set -e
export ANDROID_NDK_ROOT="$HOME/Library/Android/sdk/ndk/27.1.12297006"
API=24
BUILD_DIR=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/openal-build-arm64
SRC=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/openal-soft
TOOLCHAIN_FILE="$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake"
OUT=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/openal-android

rm -rf "$BUILD_DIR" "$OUT"
mkdir -p "$BUILD_DIR" "$OUT"

export PATH="$HOME/Library/Android/sdk/cmake/3.22.1/bin:$PATH"

cmake -S "$SRC" -B "$BUILD_DIR" \
  -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN_FILE" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-${API} \
  -DANDROID_STL=c++_static \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384" \
  -DALSOFT_BACKEND_OSS=OFF \
  -DALSOFT_BACKEND_OPENSL=ON \
  -DALSOFT_BACKEND_WAVE=OFF \
  -DALSOFT_EXAMPLES=OFF \
  -DALSOFT_TESTS=OFF \
  -DALSOFT_UTILS=OFF \
  -DALSOFT_INSTALL=ON \
  -DCMAKE_INSTALL_PREFIX="$OUT"

cmake --build "$BUILD_DIR" --config Release -j$(sysctl -n hw.ncpu)
cmake --install "$BUILD_DIR" --config Release

echo "===built artifacts==="
ls -lh "$OUT/lib/libopenal.so"
echo "===verify no libc++_shared dependency==="
"$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-readelf" -d "$OUT/lib/libopenal.so" | grep NEEDED
