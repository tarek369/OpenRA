#!/bin/bash
set -e
export ANDROID_NDK_ROOT="$HOME/Library/Android/sdk/ndk/27.1.12297006"
API=24
BUILD_DIR=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/freetype-build-arm64
SRC=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/freetype
TOOLCHAIN_FILE="$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake"
OUT=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/freetype-android

rm -rf "$BUILD_DIR" "$OUT"
mkdir -p "$BUILD_DIR" "$OUT"

# NDK r27 supports Android 16's 16 KB page-size requirement via this linker flag.
cmake -S "$SRC" -B "$BUILD_DIR" \
  -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN_FILE" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-${API} \
  -DANDROID_STL=c++_shared \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384" \
  -DBUILD_SHARED_LIBS=ON \
  -DFT_DISABLE_BZIP2=ON \
  -DFT_DISABLE_BROTLI=ON \
  -DFT_DISABLE_HARFBUZZ=ON \
  -DFT_DISABLE_PNG=ON \
  -DFT_DISABLE_ZLIB=OFF \
  -DCMAKE_INSTALL_PREFIX="$OUT"

cmake --build "$BUILD_DIR" --config Release -j$(sysctl -n hw.ncpu)
cmake --install "$BUILD_DIR" --config Release

echo "===verify 16 KB page alignment==="
SO="$OUT/lib/libfreetype.so"
OBJDUMP="$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-readobj"
"$OBJDUMP" -l "$SO" | grep -i "PageSize\|Load [0-9]" | head -5
echo "===built==="; ls -lh "$SO"
