# Build environment for the Linux WireView Mini (packaging/build-mini.sh).
#
# A NativeAOT executable links against the glibc of the machine that builds
# it, so WireView Mini is built on Ubuntu 22.04 (glibc 2.35): the binary then
# runs on every distro the packages target (Ubuntu 24.04+, Debian 13,
# Fedora 43+, Arch, the Flatpak runtime). The .NET 8 SDK comes from
# packages.microsoft.com; Ubuntu's own dotnet packages are pinned away so apt
# cannot mix the two feeds.
FROM ubuntu:22.04
ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
RUN apt-get update -qq \
 && apt-get install -y -qq --no-install-recommends ca-certificates wget \
 && wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/pmc.deb \
 && dpkg -i /tmp/pmc.deb && rm /tmp/pmc.deb \
 && printf 'Package: dotnet* aspnet* netstandard*\nPin: origin "archive.ubuntu.com"\nPin-Priority: -10\n\nPackage: dotnet* aspnet* netstandard*\nPin: origin "security.ubuntu.com"\nPin-Priority: -10\n' \
      > /etc/apt/preferences.d/dotnet-pmc \
 && apt-get update -qq \
 && apt-get install -y -qq --no-install-recommends dotnet-sdk-8.0 clang zlib1g-dev binutils \
 && rm -rf /var/lib/apt/lists/*
