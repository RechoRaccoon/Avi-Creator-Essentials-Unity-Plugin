"""
Builds a VRChat Creator Companion (VPM) listing from this repository's GitHub releases.
Every release that has a package zip attached becomes one version in index.json.
"""
import hashlib
import io
import json
import os
import subprocess
import sys
import urllib.request
import zipfile

out_dir = sys.argv[1]
repo = os.environ["REPO"]
owner, name = repo.split("/")
pages_url = f"https://{owner.lower()}.github.io/{name}"

releases = json.loads(subprocess.check_output(
    ["gh", "api", f"repos/{repo}/releases", "--paginate"], text=True))

packages = {}
for rel in releases:
    if rel.get("draft"):
        continue
    for asset in rel.get("assets", []):
        if not asset["name"].endswith(".zip"):
            continue
        url = asset["browser_download_url"]
        data = urllib.request.urlopen(url).read()
        try:
            manifest = json.loads(zipfile.ZipFile(io.BytesIO(data)).read("package.json"))
        except KeyError:
            continue
        manifest["url"] = url
        manifest["zipSHA256"] = hashlib.sha256(data).hexdigest()
        packages.setdefault(manifest["name"], {"versions": {}})["versions"][manifest["version"]] = manifest

listing = {
    "name": "Avi Creator Essentials",
    "id": f"com.{owner.lower()}.vpm",
    "url": f"{pages_url}/index.json",
    "author": owner,
    "packages": packages,
}

os.makedirs(out_dir, exist_ok=True)
with open(os.path.join(out_dir, "index.json"), "w", encoding="utf-8") as f:
    json.dump(listing, f, indent=2)

vcc_link = "vcc://vpm/addRepo?url=" + listing["url"]
html = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Avi Creator Essentials</title>
<style>
body{{font-family:system-ui,sans-serif;background:#383838;color:#d2d2d2;max-width:640px;margin:48px auto;padding:0 16px;line-height:1.5}}
a.button{{display:inline-block;background:#3a79bb;color:#fff;padding:10px 18px;border-radius:4px;text-decoration:none;font-weight:600}}
code{{background:#2a2a2a;padding:2px 6px;border-radius:3px;word-break:break-all}}
a{{color:#7fb3ff}}
</style></head><body>
<h1>Avi Creator Essentials</h1>
<p>Texture painting and bone posing tools for VRChat avatars in Unity.</p>
<p><a class="button" href="{vcc_link}">Add to VRChat Creator Companion</a></p>
<p>If the button doesn't open VCC, open VCC › Settings › Packages › Add Repository and paste:<br><code>{listing['url']}</code></p>
<p><a href="https://github.com/{repo}">Source and instructions on GitHub</a></p>
</body></html>"""
with open(os.path.join(out_dir, "index.html"), "w", encoding="utf-8") as f:
    f.write(html)
print(f"Listed {sum(len(p['versions']) for p in packages.values())} versions")
