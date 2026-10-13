"""Prints the CHANGELOG.md section for one version (used as release notes)."""
import re
import sys

version = sys.argv[1]
text = open("CHANGELOG.md", encoding="utf-8").read()
m = re.search(r"^## " + re.escape(version) + r"\s*$(.*?)(?=^## |\Z)", text, re.M | re.S)
print(m.group(1).strip() if m else "See CHANGELOG.md")
