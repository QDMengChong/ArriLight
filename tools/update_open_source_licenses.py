"""Collect license texts for the restored mobile project using only Python's standard library.

Run after dotnet restore. Package notices are copied from NuGet; upstream-only
licenses are fetched once and cached as attributed source files in the repository.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import urllib.request
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "ArriLight/Resources/Raw/OpenSourceLicenses"
SOURCES = Path(__file__).resolve().parent / "license-sources"
UPSTREAM = {
    "CommunityToolkit.Maui": ("communitytoolkit-maui.txt", "https://raw.githubusercontent.com/CommunityToolkit/Maui/95a89987c542fef3d868421940533e59a2129caa/LICENSE"),
    "CommunityToolkit.Maui.Core": ("communitytoolkit-maui.txt", "https://raw.githubusercontent.com/CommunityToolkit/Maui/95a89987c542fef3d868421940533e59a2129caa/LICENSE"),
    "Plugin.BLE": ("plugin-ble.txt", "https://raw.githubusercontent.com/dotnet-bluetooth-le/dotnet-bluetooth-le/4802d4a7f24105f66117566f3cc17da4d9ffef7e/LICENSE"),
    "ZXing.Net.Maui": ("zxing-net-maui.txt", "https://raw.githubusercontent.com/redth/ZXing.Net.Maui/8add6b40723f68f0226c1377ab9c5450d726f977/LICENSE"),
    "ZXing.Net.Maui.Controls": ("zxing-net-maui.txt", "https://raw.githubusercontent.com/redth/ZXing.Net.Maui/8add6b40723f68f0226c1377ab9c5450d726f977/LICENSE"),
    "ZXing.Net": ("zxing-net.txt", "https://raw.githubusercontent.com/micjahn/ZXing.Net/master/COPYING"),
}
SOURCE_VERSIONS = {
    "CommunityToolkit.Maui": "15.0.1", "CommunityToolkit.Maui.Core": "15.0.1",
    "Plugin.BLE": "3.2.1", "ZXing.Net": "0.16.11",
    "ZXing.Net.Maui": "0.10.4", "ZXing.Net.Maui.Controls": "0.10.4",
}


def copy_document(data, name, source):
    # Deduplicate shared terms without dropping any package-specific notices.
    filename = hashlib.sha256(data).hexdigest()[:24] + ".txt"
    path = OUTPUT / "texts" / filename
    if not path.exists() or path.read_bytes() != data:
        path.write_bytes(data)
    return {"name": name, "assetPath": "OpenSourceLicenses/texts/" + filename, "source": source}


def upstream_document(package, version):
    if SOURCE_VERSIONS[package] != version:
        raise ValueError(f"Review the upstream license source for {package} {version} before updating SOURCE_VERSIONS.")
    filename, url = UPSTREAM[package]
    path = SOURCES / filename
    if not path.exists():
        request = urllib.request.Request(url, headers={"User-Agent": "ArriLight-license-catalog"})
        with urllib.request.urlopen(request, timeout=30) as response:
            data = response.read()
        if len(data) < 500 or b"<html" in data.lower():
            raise ValueError(f"Unexpected license response: {url}")
        path.write_bytes(data)
    return copy_document(path.read_bytes(), "LICENSE", url)


def latest_version(directory, prefix=""):
    versions = [p for p in directory.iterdir() if p.is_dir() and p.name.startswith(prefix)]
    return max(versions, key=lambda p: tuple(int(n) for n in re.findall(r"\d+", p.name)))


def font_names(path):
    # Read original TTF name records instead of assuming the license of today's font release.
    data = path.read_bytes()
    tables = struct.unpack_from(">H", data, 4)[0]
    for i in range(tables):
        tag, _, offset, _ = struct.unpack_from(">4sIII", data, 12 + i * 16)
        if tag != b"name":
            continue
        _, count, strings = struct.unpack_from(">HHH", data, offset)
        names = {}
        for j in range(count):
            platform, _, language, name, length, start = struct.unpack_from(">HHHHHH", data, offset + 6 + j * 12)
            if platform == 3 and language == 1033 and name in (0, 5, 13, 14):
                names[name] = data[offset + strings + start:offset + strings + start + length].decode("utf-16-be")
        return names
    raise ValueError(f"Missing font name table: {path}")


def generate(dotnet_root):
    assets = json.loads((ROOT / "ArriLight/obj/project.assets.json").read_text(encoding="utf-8"))
    OUTPUT.mkdir(parents=True, exist_ok=True)
    (OUTPUT / "texts").mkdir(exist_ok=True)
    SOURCES.mkdir(parents=True, exist_ok=True)
    direct = {n.attrib["Include"] for n in ET.parse(ROOT / "ArriLight/ArriLight.csproj").iter("PackageReference")}
    components = []
    for key, library in sorted(assets["libraries"].items()):
        if library["type"] != "package":
            continue
        folder = next(Path(p) / library["path"] for p in assets["packageFolders"] if (Path(p) / library["path"]).exists())
        metadata = ET.parse(next(folder.glob("*.nuspec"))).getroot().find("{*}metadata")
        value = lambda name: metadata.findtext("{*}" + name, "")
        package, version = value("id"), value("version")
        license_node = metadata.find("{*}license")
        if license_node is None or not license_node.text:
            raise ValueError(f"No license declared for {key}")
        documents = []
        notice_files = [f for f in library["files"] if re.search(r"(?i)^(license|copying|.*notice)", Path(f).name)]
        for filename in sorted(notice_files, key=lambda f: ("notice" in f.lower(), f)):
            documents.append(copy_document((folder / filename).read_bytes(), Path(filename).name,
                                          f"https://www.nuget.org/packages/{package}/{version}"))
        if not any(re.match(r"(?i)^(license|copying)", d["name"]) for d in documents):
            if package in UPSTREAM:
                documents.insert(0, upstream_document(package, version))
            elif package.startswith("Microsoft.Extensions.") or package == "Microsoft.NET.ILLink.Tasks":
                runtime = latest_version(dotnet_root / "packs/Microsoft.NETCore.App.Runtime.Mono.android-arm64", "10.")
                documents.insert(0, copy_document((runtime / "LICENSE.TXT").read_bytes(), "LICENSE.TXT",
                                                  "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"))
            else:
                raise ValueError(f"No complete license text for {key}; add an attributed upstream source.")
        components.append({"id": key, "name": package, "version": version,
                           "license": license_node.text, "projectUrl": value("projectUrl"),
                           "authors": value("authors"), "copyright": value("copyright"),
                           "isDirect": package in direct, "documents": documents})

    packs = dotnet_root / "packs"
    runtime = latest_version(packs / "Microsoft.NETCore.App.Runtime.Mono.android-arm64", "10.")
    android = latest_version(packs / "Microsoft.Android.Sdk.Windows")
    ios_base = max(packs.glob("Microsoft.iOS.Sdk.net10.0_*"), key=lambda p: p.name)
    ios = latest_version(ios_base)
    for name, path, url, files in [
        (".NET runtime", runtime, "https://github.com/dotnet/runtime", ["LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"]),
        (".NET for Android", android, "https://github.com/dotnet/android", ["LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"]),
        (".NET for iOS", ios, "https://github.com/dotnet/macios", ["LICENSE"]),
    ]:
        documents = [copy_document((path / f).read_bytes(), f, url) for f in files]
        components.append({"id": name, "name": name, "version": path.name, "license": "MIT",
                           "projectUrl": url, "authors": ".NET Foundation and Contributors", "copyright": "",
                           "isDirect": True, "documents": documents})

    fonts = sorted((ROOT / "ArriLight/Resources/Fonts").glob("OpenSans-*.ttf"))
    names = [font_names(p) for p in fonts]
    if not names or any("Apache License, Version 2.0" not in n.get(13, "") for n in names):
        raise ValueError("Open Sans font changed; review its embedded license before generating.")
    font_notice = "\n\n".join(f"{p.name}\n{n[0]}\n{n[5]}\n{n[13]}\n{n[14]}" for p, n in zip(fonts, names))
    apache_path = SOURCES / "apache-2.0.txt"
    if not apache_path.exists():
        with urllib.request.urlopen("https://www.apache.org/licenses/LICENSE-2.0.txt", timeout=30) as response:
            apache_path.write_bytes(response.read())
    components.append({"id": "Open Sans", "name": "Open Sans", "version": names[0][5].removeprefix("Version "),
                       "license": "Apache-2.0", "projectUrl": "https://fonts.google.com/specimen/Open+Sans",
                       "authors": "Google Corporation", "copyright": "\n".join(dict.fromkeys(n[0] for n in names)),
                       "isDirect": True, "documents": [
                           copy_document(font_notice.encode("utf-8"), "FONT NOTICES", "Embedded TTF name records"),
                           copy_document(apache_path.read_bytes(), "Apache License 2.0", "https://www.apache.org/licenses/LICENSE-2.0.txt")]})
    components.sort(key=lambda c: (not c["isDirect"], c["name"].casefold()))
    manifest = {"schemaVersion": 1, "components": components}
    (OUTPUT / "catalog.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Collected {len(components)} components and {len({d['assetPath'] for c in components for d in c['documents']})} distinct license/notice files.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    default_root = Path(shutil.which("dotnet") or "dotnet").resolve().parent
    if not (default_root / "packs").exists():
        default_root = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "dotnet"
    parser.add_argument("--dotnet-root", type=Path, default=default_root)
    args = parser.parse_args()
    generate(args.dotnet_root)
