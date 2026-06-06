# Self-contained .NET 8 application: the source tarball ships a pre-built,
# self-contained x86_64 binary bundle. There is no compilation in the RPM build
# (the COPR/mock chroot has no .NET SDK and no network), so disable all the build
# machinery that assumes a normal compiled package.
%global appdir %{_prefix}/lib/%{name}
%global debug_package %{nil}
%global __strip /bin/true
%global _build_id_links none
# Define the udev rules dir if systemd-rpm-macros didn't.
%{!?_udevrulesdir: %global _udevrulesdir %{_prefix}/lib/udev/rules.d}

Name:           wireview-linux
Version:        1.0.6.0
Release:        1%{?dist}
Summary:        Unofficial Linux GUI for the Thermal Grizzly WireView Pro II

# Not a FOSS license; redistribution for personal use / hardware interoperability.
License:        LicenseRef-Proprietary
URL:            https://github.com/emaspa/wireview-linux
Source0:        %{name}-%{version}-linux-x64.tar.gz
ExclusiveArch:  x86_64

# The bundled native libraries are private to the app directory; do not scan
# them for auto-generated Requires/Provides (they would pull in liblttng-ust etc).
AutoReqProv:    no

# Host libraries the bundled .NET runtime, SkiaSharp and Avalonia dlopen at run time.
Requires:       fontconfig
Requires:       freetype
Requires:       libX11
Requires:       libicu
# Optional: hwmon kernel module + daemon for system-wide sensor integration.
Recommends:     wireview-hwmon

%description
Unofficial Linux GUI for the Thermal Grizzly WireView Pro II GPU power monitor.
Real-time monitoring with live charts, device configuration, data logging with
CSV export, desktop notifications, and optional software shutdown on fault.

Talks to the device directly over USB serial, or via the wireview-hwmon kernel
module and daemon when present. Self-contained binary; no .NET runtime is
required on the host.

%prep
%autosetup

%build
# Nothing to build: pre-built self-contained binary shipped in the source tarball.

%install
# Application bundle
install -d %{buildroot}%{appdir}
cp -a linux-x64/. %{buildroot}%{appdir}/
find %{buildroot}%{appdir} -name '*.pdb' -delete
chmod 0755 %{buildroot}%{appdir}/WireView2

# Launcher symlink
install -d %{buildroot}%{_bindir}
ln -sf ../lib/%{name}/WireView2 %{buildroot}%{_bindir}/%{name}

# Desktop entry + icons
install -Dm0644 wireview-linux.desktop %{buildroot}%{_datadir}/applications/%{name}.desktop
install -d %{buildroot}%{_datadir}/icons
cp -a icons/hicolor %{buildroot}%{_datadir}/icons/

# udev rule (USB serial access)
install -Dm0644 udev/99-wireview.rules %{buildroot}%{_udevrulesdir}/99-wireview.rules

%files
%{appdir}/
%{_bindir}/%{name}
%{_datadir}/applications/%{name}.desktop
%{_datadir}/icons/hicolor/*/apps/%{name}.png
%{_udevrulesdir}/99-wireview.rules

%post
touch --no-create %{_datadir}/icons/hicolor &>/dev/null || :

%postun
if [ $1 -eq 0 ]; then
    touch --no-create %{_datadir}/icons/hicolor &>/dev/null || :
    gtk-update-icon-cache %{_datadir}/icons/hicolor &>/dev/null || :
    update-desktop-database &>/dev/null || :
fi

%posttrans
gtk-update-icon-cache %{_datadir}/icons/hicolor &>/dev/null || :
update-desktop-database &>/dev/null || :

%changelog
* Sat Jun 06 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 1.0.6.0-1
- Initial RPM / COPR package
- Self-contained .NET 8 GUI for the Thermal Grizzly WireView Pro II
- Second tray icon with live total power and a hwmon values dropdown
