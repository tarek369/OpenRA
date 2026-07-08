#!/bin/bash
set -e
export NDK="$HOME/Library/Android/sdk/ndk/27.1.12297006"
API=24
HOST=darwin-x86_64
TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/$HOST"
export PATH="$TOOLCHAIN/bin:$PATH"
TARGET=aarch64-linux-android
export CC="$TOOLCHAIN/bin/${TARGET}${API}-clang"
export AR="$TOOLCHAIN/bin/llvm-ar"
export RANLIB="$TOOLCHAIN/bin/llvm-ranlib"
export MYCFLAGS="-DLUA_USE_LINUX -fPIC"
export MYLDFLAGS="-Wl,-z,max-page-size=16384"
export LDFLAGS="$MYLDFLAGS"

SRC=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/lua-5.1.5
BUILD=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/lua-build-arm64
OUT=/Users/tarekhosni/Dev/POC/OpenRA/thirdparty/lua-android
rm -rf "$BUILD" "$OUT"
mkdir -p "$BUILD" "$OUT/lib"

# Build the Lua core library objects (the .so needs these, not the lua/luac executables).
cd "$SRC"
make clean >/dev/null 2>&1 || true

# Compile core objects manually to avoid the posix/Readline dependency of the full Makefile target.
CORE="lapi lcode ldebug ldo ldump lfunc lgc llex lmem lobject lopcodes lparser lstate lstring ltable ltm lundump lvm lzio"
LIB="lauxlib lbaselib ldblib liolib lmathlib loslib ltablib lstrlib loadlib linit"
OBJS=""
for f in $CORE $LIB; do
  $CC $MYCFLAGS -c src/$f.c -o "$BUILD/$f.o"
  OBJS="$OBJS $BUILD/$f.o"
done

# Link into a shared library named lua51.so so the existing dllmap resolves on Android.
$CC -shared $MYLDFLAGS -Wl,-soname,liblua51.so -o "$OUT/lib/liblua51.so" $OBJS -lm -ldl

echo "===built==="; ls -lh "$OUT/lib/liblua51.so"
echo "===verify 16 KB page alignment==="
"$TOOLCHAIN/bin/llvm-readelf" -l "$OUT/lib/liblua51.so" | grep -i "LOAD" | head -3
