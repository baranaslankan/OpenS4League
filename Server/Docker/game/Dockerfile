# Game server runtime image: the stock ASP.NET 10 runtime plus a native LZO library.
#
# SharpLzo 0.1.2 P/Invokes "MiniLzo.so" but ships it for linux-x64 only, so on linux/arm64
# (Apple Silicon, Oracle Ampere) its type initializer throws. The distro's liblzo2 exports the
# same LZO1X entry points (__lzo_init_v2, lzo1x_1_compress, lzo1x_decompress_safe), so it is
# linked under that name on the default library path. On x64 the copy SharpLzo ships in the app
# folder is found first and this one is never used. See docs/M0-APPLE-SILICON.md.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends liblzo2-2 \
 && rm -rf /var/lib/apt/lists/* \
 && ln -s "$(find /usr/lib /lib -name 'liblzo2.so.2' | head -n1)" /usr/lib/MiniLzo.so \
 && test -e /usr/lib/MiniLzo.so
