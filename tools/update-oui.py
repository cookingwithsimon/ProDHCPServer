#!/usr/bin/env python3
"""Refreshes src/ProDHCPServer.Core/Resources/oui.txt.gz from the IEEE MA-L registry.

Usage: python3 tools/update-oui.py [path-to-oui.csv]
Without an argument it downloads https://standards-oui.ieee.org/oui/oui.csv.
Output format: one "AABBCC<TAB>Organization" line per assignment, gzip-compressed.
"""
import csv, gzip, io, pathlib, sys, urllib.request

URL = "https://standards-oui.ieee.org/oui/oui.csv"
OUT = pathlib.Path(__file__).resolve().parent.parent / "src/ProDHCPServer.Core/Resources/oui.txt.gz"

def main():
    if len(sys.argv) > 1:
        text = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
    else:
        req = urllib.request.Request(URL, headers={"User-Agent": "ProDHCPServer-oui-updater"})
        text = urllib.request.urlopen(req, timeout=60).read().decode("utf-8")
    rows = {}
    for row in csv.DictReader(io.StringIO(text)):
        oui = row["Assignment"].strip().upper()
        name = " ".join(row["Organization Name"].split())
        if len(oui) == 6 and name:
            rows[oui] = name
    data = "".join(f"{k}\t{v}\n" for k, v in sorted(rows.items())).encode()
    with gzip.open(OUT, "wb", 9) as f:
        f.write(data)
    print(f"Wrote {len(rows)} entries to {OUT}")

if __name__ == "__main__":
    main()
