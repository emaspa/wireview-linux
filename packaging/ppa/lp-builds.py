#!/usr/bin/python3
"""Show (and optionally wait for / retry) the PPA builds of a version.

Usage:  packaging/ppa/lp-builds.py <version> [--wait] [--retry] [--source NAME]
  e.g.  packaging/ppa/lp-builds.py 1.2.5.0 --wait --retry
        packaging/ppa/lp-builds.py 1.8.0 --wait --source wireview-hwmon

Looks up the per-series versions in ppa:sparvoli/wireview-hwmon and prints the
source publishing status and each build's state and URL. The default source is
wireview-linux, whose versions are <version>~<series>1; wireview-hwmon (the
daemon, same PPA) uses <version>~<release>.1, e.g. 1.8.0~26.04.1.
--source NAME  the source package name (wireview-linux or wireview-hwmon).
--wait   poll every 60 s until every build has finished (and the source is published).
--retry  retry a failed build once if Launchpad attached no build log
         (known builder infrastructure flake; a real failure has a log).

Run with /usr/bin/python3 (it has launchpadlib; a Homebrew python3 does not).
Credentials: ~/.config/launchpadlib-wireview.creds (write access, "Change anything").
"""
import os
import sys
import time

from launchpadlib.launchpad import Launchpad

SERIES = ("resolute", "noble")
SERIES_RELEASE = {"resolute": "26.04", "noble": "24.04"}
CREDS = os.path.expanduser("~/.config/launchpadlib-wireview.creds")
DONE = {"Successfully built", "Failed to build", "Chroot problem",
        "Failed to upload", "Cancelled build", "Build for superseded Source"}


def main():
    argv = sys.argv[1:]
    source = "wireview-linux"
    if "--source" in argv:
        i = argv.index("--source")
        source = argv[i + 1]
        del argv[i:i + 2]
    args = [a for a in argv if not a.startswith("--")]
    if len(args) != 1:
        sys.exit(__doc__)
    version = args[0]
    wait, retry = "--wait" in sys.argv, "--retry" in sys.argv

    lp = Launchpad.login_with("wireview", "production", credentials_file=CREDS, version="devel")
    ppa = lp.people["sparvoli"].getPPAByName(name="wireview-hwmon")
    retried = set()

    while True:
        pending = False
        for series in SERIES:
            ver = (f"{version}~{SERIES_RELEASE[series]}.1" if source == "wireview-hwmon"
                   else f"{version}~{series}1")
            srcs = ppa.getPublishedSources(source_name=source, version=ver, exact_match=True)
            if len(srcs) == 0:
                print(f"{ver}: not in the PPA yet (upload still being processed?)")
                pending = True
                continue
            src = srcs[0]
            print(f"{ver}: source {src.status}")
            if src.status == "Pending":
                pending = True
            builds = src.getBuilds()
            if len(builds) == 0:
                pending = True
            for b in builds:
                print(f"  {b.arch_tag}: {b.buildstate}  {b.web_link}")
                if b.buildstate not in DONE:
                    pending = True
                elif (retry and b.buildstate == "Failed to build" and not b.build_log_url
                      and b.self_link not in retried and b.can_be_retried):
                    print("    no build log attached: retrying once")
                    b.retry()
                    retried.add(b.self_link)
                    pending = True
        if not (wait and pending):
            break
        print("-- waiting 60 s --")
        time.sleep(60)


if __name__ == "__main__":
    main()
