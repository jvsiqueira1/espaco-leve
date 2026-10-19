#!/bin/sh
set -eu
base="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
case "$(uname -s)" in
  Darwin) cc -dynamiclib -O2 -Wall -Wextra -Werror "$base/native/safe_unix.c" -o "$base/native/libespacoleve_native.dylib" ;;
  Linux) cc -shared -fPIC -O2 -Wall -Wextra -Werror "$base/native/safe_unix.c" -o "$base/native/libespacoleve_native.so" ;;
  *) echo "Unsupported native platform" >&2; exit 1 ;;
esac
