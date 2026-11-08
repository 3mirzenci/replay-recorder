#!/bin/sh
# Builds ffmpeg-capture.exe: a small ffmpeg used only for recording (WGC capture -> NVENC, audio pipes,
# AAC, MPEG-TS). Stock ffmpeg 9.0.2 plus gfxcapture.patch. LGPL only: no GPL or nonfree parts.
#   sh ffmpeg/build.sh <work dir>     (needs a mingw-w64 gcc/g++ with WinRT headers, make, nasm, git)
# Output: <work dir>/out/ffmpeg-capture.exe
set -e
here=$(cd "$(dirname "$0")" && pwd)
work=${1:-$here/.work}
tag=n9.0.2
nvheaders=n12.1.14.1  # SDK 12.1: works with NVIDIA drivers 531 and newer
mkdir -p "$work" && cd "$work"
export TMPDIR="$work/tmp"; mkdir -p "$TMPDIR"
MAKE=$(command -v make || command -v mingw32-make)

[ -d ffmpeg ] || git clone -q --depth 1 --branch $tag https://github.com/FFmpeg/FFmpeg.git ffmpeg
git -C ffmpeg checkout -q -- . && git -C ffmpeg apply "$here/gfxcapture.patch"

[ -d nvh ] || git clone -q --depth 1 --branch $nvheaders https://github.com/FFmpeg/nv-codec-headers.git nvh
$MAKE -C nvh PREFIX="$work/deps" install >/dev/null

# ffnvcodec is header-only; this stands in for pkg-config so no pkg-config install is needed.
cat > pkgconf.sh <<EOF
#!/bin/sh
case "\$*" in *--version*) echo 0.29; exit 0;; *ffnvcodec*) ;; *) exit 1;; esac
case "\$*" in *--cflags*) echo "-I$work/deps/include";; esac
exit 0
EOF
chmod +x pkgconf.sh

mkdir -p build && cd build
../ffmpeg/configure \
  --pkg-config="$work/pkgconf.sh" \
  --disable-everything --disable-autodetect --disable-shared --enable-static \
  --disable-doc --disable-network --disable-debug --disable-ffplay --disable-ffprobe \
  --enable-w32threads --enable-d3d11va --enable-ffnvcodec --enable-nvenc \
  --enable-filter=gfxcapture,setpts,fps,highpass,agate,acompressor,alimiter,asplit,pan,amix,aresample,aformat,anull,null,format \
  --enable-encoder=h264_nvenc,hevc_nvenc,aac \
  --enable-decoder=pcm_f32le --enable-demuxer=pcm_f32le \
  --enable-muxer=mpegts,segment,matroska,null \
  --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb \
  --enable-protocol=file,pipe \
  --extra-ldflags=-static --extra-cflags="-I$work/deps/include" > configure.log
$MAKE -j"$(nproc 2>/dev/null || echo 4)" ffmpeg.exe > make.log
strip ffmpeg.exe
mkdir -p "$work/out"
cp ffmpeg.exe "$work/out/ffmpeg-capture.exe"
cp ../ffmpeg/COPYING.LGPLv2.1 "$work/out/LICENSE-ffmpeg.txt"
cp "$here/gfxcapture.patch" "$here/SOURCE.txt" "$work/out/"
echo "built $work/out/ffmpeg-capture.exe"
