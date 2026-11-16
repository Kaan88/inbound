#!/usr/bin/env python3
"""Reports pinned third-party tools that are behind their upstream release (.github/workflows/upkeep.yml).

Dependabot covers NuGet, npm, Docker and GitHub Actions (.github/dependabot.yml). These pins it can't see:
libtorrent (the worker's overlay port), the vcpkg baseline, rclone, 7-Zip, appimagetool and nfpm.

Reads each pin from the repository, asks GitHub for the newest release, and prints a Markdown report. With --out it
also writes the report to a file. Exit code 0 always; the report says what is behind. Needs GH_TOKEN for the API.
Nothing here changes a pin: an update is a decision (and a test) of its own.
"""
import json
import os
import re
import sys
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOKEN = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
VCPKG_STALE_DAYS = 90


def api(path):
    request = urllib.request.Request(f"https://api.github.com/{path}", headers={"Accept": "application/vnd.github+json"})
    if TOKEN:
        request.add_header("Authorization", f"Bearer {TOKEN}")
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def version_tuple(text):
    numbers = re.findall(r"\d+", text)
    return tuple(int(n) for n in numbers) if numbers else ()


def newest_release(repo, pattern=r"^v?\d+(\.\d+)+$"):
    """The highest stable release tag of repo that matches pattern (drafts, pre-releases and 'continuous' skipped)."""
    tags = [r["tag_name"] for r in api(f"repos/{repo}/releases?per_page=100")
            if not r["draft"] and not r["prerelease"] and re.match(pattern, r["tag_name"])]
    return max(tags, key=version_tuple) if tags else None


def read(path, regex, what):
    match = re.search(regex, (ROOT / path).read_text(encoding="utf-8"), re.MULTILINE)
    if not match:
        raise SystemExit(f"Could not find {what} in {path}; update scripts/check-pins.py.")
    return match.group(1)


def main():
    rows = []  # (tool, pinned, upstream, behind, where)

    def compare(tool, pinned, repo, where, display=lambda tag: tag.lstrip("v"), pattern=r"^v?\d+(\.\d+)+$"):
        tag = newest_release(repo, pattern)
        upstream = display(tag) if tag else "?"
        behind = tag is not None and version_tuple(upstream) > version_tuple(pinned)
        rows.append((tool, pinned, upstream, behind, where))

    compare("libtorrent", read("native/torrent-worker/ports/libtorrent/vcpkg.json", r'"version":\s*"([^"]+)"', "libtorrent's version"),
            "arvidn/libtorrent", "native/torrent-worker/ports/libtorrent (overlay port: version, SHA512)")
    compare("rclone", ".".join(re.findall(r"\d+", read("src/DownloadManager.Sites/RcloneUpdater.cs",
            r"Version Version = new\(([\d, ]+)\)", "RclonePinned.Version"))),
            "rclone/rclone", "src/DownloadManager.Sites/RcloneUpdater.cs (RclonePinned), third_party/rclone, Dockerfile")
    sevenzip = read("Dockerfile", r"^ARG SEVENZIP_VERSION=(\d+)", "SEVENZIP_VERSION")
    compare("7-Zip", f"{sevenzip[:-2]}.{sevenzip[-2:]}", "ip7z/7zip",
            "Dockerfile (SEVENZIP_VERSION), third_party/7-Zip", pattern=r"^\d+\.\d+$")
    compare("appimagetool", read(".github/workflows/appimage.yml", r"appimagetool/releases/download/([\d.]+)/", "appimagetool's version"),
            "AppImage/appimagetool", ".github/workflows/appimage.yml (URL and SHA-256)")
    compare("nfpm", read(".github/workflows/appimage.yml", r"nfpm/releases/download/v([\d.]+)/", "nfpm's version"),
            "goreleaser/nfpm", ".github/workflows/appimage.yml (URL and SHA-256)")

    # vcpkg has no versions of its own: the baseline is a commit. Old means Boost and the other ports are old too.
    baseline = read("native/torrent-worker/vcpkg.json", r'"builtin-baseline":\s*"([0-9a-f]{40})"', "the vcpkg baseline")
    compared = api(f"repos/microsoft/vcpkg/compare/{baseline}...master")
    committed = api(f"repos/microsoft/vcpkg/commits/{baseline}")["commit"]["committer"]["date"]
    age = (datetime.now(timezone.utc) - datetime.fromisoformat(committed.replace("Z", "+00:00"))).days
    latest_tag = newest_release("microsoft/vcpkg", r"^\d{4}\.\d{2}\.\d{2}$") or "?"
    rows.append(("vcpkg baseline", f"{baseline[:9]} ({committed[:10]}, {age} days old)",
                 f"{compared['ahead_by']} commits newer; latest release {latest_tag}", age > VCPKG_STALE_DAYS,
                 "native/torrent-worker/vcpkg.json (builtin-baseline) and the macOS/Linux/Windows worker builds' VCPKG_COMMIT"))

    behind = [r for r in rows if r[3]]
    lines = ["Pinned tools that Dependabot can't see, checked by `scripts/check-pins.py`.", "",
             "| Tool | Pinned | Upstream | Behind | Pinned in |", "| --- | --- | --- | --- | --- |"]
    lines += [f"| {tool} | {pinned} | {upstream} | {'**yes**' if late else 'no'} | {where} |" for tool, pinned, upstream, late, where in rows]
    lines += ["", f"vcpkg counts as behind when its baseline is over {VCPKG_STALE_DAYS} days old.",
              "Updating a pin is done by hand: new URL and checksum, a build, and the tests that use the tool."]
    report = "\n".join(lines)
    print(report)
    if "--out" in sys.argv:
        Path(sys.argv[sys.argv.index("--out") + 1]).write_text(report + "\n", encoding="utf-8")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
            out.write(f"behind={len(behind)}\n")


if __name__ == "__main__":
    main()
