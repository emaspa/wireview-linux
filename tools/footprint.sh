#!/usr/bin/env bash
# Memory and CPU footprint of a running process, sampled over a period.
#
#   tools/footprint.sh <pid|name> [seconds] [--maps]
#
# Prints RSS, PSS, anonymous PSS, thread count and the CPU share over the
# window (one line per 10 s sample, then a summary). With --maps the resident
# size per mapped file is listed at the end, which shows where the memory is
# (GC heap and Skia surfaces are anonymous; Mesa, ICU, JIT double mapping and
# managed assemblies each show up under their file).
#
# A name is resolved with pgrep -x (first match).
set -euo pipefail

target=${1:?usage: footprint.sh <pid|name> [seconds] [--maps]}
secs=${2:-60}
maps=${3:-}

if [[ $target =~ ^[0-9]+$ ]]; then pid=$target
else pid=$(pgrep -x -n "$target") || { echo "no process named $target" >&2; exit 1; }
fi
[[ -r /proc/$pid/smaps_rollup ]] || { echo "cannot read /proc/$pid" >&2; exit 1; }

clk=$(getconf CLK_TCK)
cmd=$(tr '\0' ' ' < /proc/$pid/cmdline | cut -c1-80)
echo "pid $pid: $cmd"
echo "env: $(tr '\0' '\n' < /proc/$pid/environ 2>/dev/null | grep -E '^(DOTNET_|AVALONIA_)' | tr '\n' ' ')"
printf '%-6s %8s %8s %8s %7s %6s\n' t RSS_MB PSS_MB ANON_MB thr CPU%

ticks() { awk '{print $14+$15}' /proc/$pid/stat; }
mem() {
    awk -v k="$1" '$1==k":"{printf "%.1f", $2/1024}' /proc/$pid/smaps_rollup
}
t0=$(ticks); start=$(date +%s); last=$t0; lastt=$start
elapsed=0
while (( elapsed < secs )); do
    sleep 10
    now=$(date +%s); t=$(ticks)
    cpu=$(awk -v d=$((t-last)) -v s=$((now-lastt)) -v c=$clk 'BEGIN{printf "%.2f", 100*d/(s*c)}')
    printf '%-6s %8s %8s %8s %7s %6s\n' "$((now-start))" "$(mem Rss)" "$(mem Pss)" "$(mem Pss_Anon)" \
        "$(grep -c . /proc/$pid/task/*/stat 2>/dev/null | wc -l)" "$cpu"
    last=$t; lastt=$now; elapsed=$((now-start))
done
t1=$(ticks)
echo "avg CPU over ${elapsed}s: $(awk -v d=$((t1-t0)) -v s=$elapsed -v c=$clk 'BEGIN{printf "%.2f%%", 100*d/(s*c)}') of one core"

if [[ $maps == --maps ]]; then
    echo "resident per mapping (MB):"
    awk '/^[0-9a-f]+-[0-9a-f]+ /{name=$6; if(name=="") name="[anon]"}
         /^Rss:/{rss[name]+=$2}
         END{for(n in rss) if (rss[n] > 1024) printf "%8.1f %s\n", rss[n]/1024, n}' /proc/$pid/smaps | sort -rn
fi
