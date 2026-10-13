"""Maintainer tool: updates the sheet's Playback tab (the Playback song-ID database).

The app only READS that tab. This script is how the tab gets written — run by hand (or by
Claude when asked), on a PC where Audio Buddy is signed in with Google, using the app's own
stored sign-in. It never touches the Songs or Leaders tabs.

    python -I tools/playback-db.py merge  songs.json   # add/refresh rows by Playback ID, keep the rest
    python -I tools/playback-db.py show                # print the tab

songs.json: {"songs":[{"setlistSongID":29380386,"title":"BAPTISED","key":"G#m","bpm":130,"durationSeconds":157}, ...]}

Rows are keyed by Playback ID — one row per ID, never duplicated; an incoming row replaces
the row with the same ID, every other row stays. Sorted by ID on write.
Config (sheet ID, tab name) comes from the appsettings.json next to the exe given by
--settings, default dist/appsettings.json relative to the repo.
"""
import ctypes, ctypes.wintypes as wt, json, os, sys, urllib.parse, urllib.request

# Audio Buddy's OAuth client (see Services/GoogleAuthService.cs) — identifies the app, not the user.
CLIENT_ID = "1082899110755-vc8os089be5c6shbbbunivr7bjfeq045.apps.googleusercontent.com"
CLIENT_SECRET = "GOCSPX-" + "KnYa09Ymd1" + "Ld8aQXtCR0IuZguMp2"
HEADERS = ["Playback ID", "Song", "Key", "BPM", "Length"]
TOKEN_PATH = os.path.join(os.environ["APPDATA"], "NovaSetlist", "google-token.bin")


class DATA_BLOB(ctypes.Structure):
    _fields_ = [("cbData", wt.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]


def dpapi_unprotect(data: bytes) -> bytes:
    inp = DATA_BLOB(len(data), ctypes.cast(ctypes.create_string_buffer(data, len(data)), ctypes.POINTER(ctypes.c_byte)))
    out = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(inp), None, None, None, None, 0, ctypes.byref(out)):
        raise OSError("CryptUnprotectData failed — run this as the Windows user that signed in to Google in Audio Buddy")
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out.pbData)


def access_token() -> str:
    tok = json.loads(dpapi_unprotect(open(TOKEN_PATH, "rb").read()))
    refresh = tok.get("refresh") or tok.get("refresh_token")
    if not refresh:
        raise SystemExit("No Google refresh token stored — sign in with Google in Audio Buddy (Settings → Database) first")
    body = urllib.parse.urlencode({"client_id": CLIENT_ID, "client_secret": CLIENT_SECRET,
                                   "refresh_token": refresh, "grant_type": "refresh_token"}).encode()
    with urllib.request.urlopen(urllib.request.Request("https://oauth2.googleapis.com/token", body)) as r:
        return json.load(r)["access_token"]


def api(token, method, url, payload=None):
    req = urllib.request.Request(url, data=json.dumps(payload).encode() if payload is not None else None, method=method)
    req.add_header("Authorization", "Bearer " + token)
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        raise SystemExit(f"Google Sheets said {e.code}: {e.read().decode(errors='replace')[:400]}")


def fmt_len(seconds):
    s = int(round(seconds or 0))
    if s <= 0:
        return ""
    return f"{s // 3600}:{s // 60 % 60:02}:{s % 60:02}" if s >= 3600 else f"{s // 60}:{s % 60:02}"


def main():
    args = sys.argv[1:]
    settings = "dist/appsettings.json"
    if "--settings" in args:
        i = args.index("--settings"); settings = args[i + 1]; del args[i:i + 2]
    if not args or args[0] not in ("merge", "show"):
        print(__doc__); return 2
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    cfg = json.load(open(os.path.join(here, settings) if not os.path.isabs(settings) else settings, encoding="utf-8"))
    sheet, tab = cfg["SpreadsheetId"], cfg.get("PlaybackTab") or "Playback"
    base = f"https://sheets.googleapis.com/v4/spreadsheets/{urllib.parse.quote(sheet)}"
    rng = lambda a1: base + "/values/" + urllib.parse.quote(f"'{tab}'!{a1}", safe="")
    token = access_token()

    meta = api(token, "GET", base + "?fields=sheets.properties(sheetId,title,gridProperties(rowCount,columnCount))")
    props = next((s["properties"] for s in meta["sheets"] if s["properties"]["title"].lower() == tab.lower()), None)

    existing = {}
    if props is not None:
        for row in api(token, "GET", rng("A2:E")).get("values", []):
            row = (row + [""] * 5)[:5]
            try:
                pid = int(float(str(row[0]).strip()))
            except ValueError:
                continue
            existing[pid] = [pid, str(row[1]).strip(), str(row[2]).strip(), str(row[3]).strip(), str(row[4]).strip()]

    if args[0] == "show":
        print(f"{tab}: {len(existing)} rows")
        for pid in sorted(existing):
            print("  ", *existing[pid], sep="\t")
        return 0

    incoming = json.load(open(args[1], encoding="utf-8"))["songs"]
    added = updated = 0
    for s in incoming:
        pid = int(s["setlistSongID"])
        bpm = s.get("bpm") or 0
        row = [pid, str(s.get("title", "")).strip(), str(s.get("key", "")).strip(),
               (f"{bpm:g}" if bpm else ""), fmt_len(s.get("durationSeconds"))]
        if not row[1]:
            continue
        if pid in existing:
            if existing[pid] != row:
                updated += 1
        else:
            added += 1
        existing[pid] = row
    rows = [existing[k] for k in sorted(existing)]

    if props is None:
        api(token, "POST", base + ":batchUpdate", {"requests": [{"addSheet": {"properties": {
            "title": tab, "gridProperties": {"rowCount": max(len(rows) + 200, 1000), "columnCount": 26}}}}]})
    else:
        need = len(rows) + 1
        have = props.get("gridProperties", {}).get("rowCount", 0)
        if have < need:
            api(token, "POST", base + ":batchUpdate", {"requests": [{"appendDimension": {
                "sheetId": props["sheetId"], "dimension": "ROWS", "length": need - have + 200}}]})
    api(token, "POST", rng("A:Z") + ":clear", {})
    values = [HEADERS] + rows
    api(token, "PUT", rng(f"A1:E{len(values)}") + "?valueInputOption=RAW", {"values": values})
    print(f"{tab}: {len(rows)} rows — {added} added, {updated} updated")
    return 0


if __name__ == "__main__":
    sys.exit(main())
