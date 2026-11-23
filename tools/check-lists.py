import os

d = r"C:\Users\andre\Desktop\lists"
up = r"D:\PROJECT\OpenCode\Zapret\src\zapret\lists"


def read_text(path):
    raw = open(path, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig", errors="replace")
    return text, bom, raw.count(b"\r\n"), raw.count(b"\n")


def entries(path):
    text = open(path, encoding="utf-8-sig", errors="replace").read()
    return [l.strip() for l in text.replace("\r\n", "\n").split("\n")
            if l.strip() and not l.strip().startswith("#")]


for name in sorted(os.listdir(d)):
    path = os.path.join(d, name)
    text, bom, crlf, lf = read_text(path)
    lines = [l.strip() for l in text.replace("\r\n", "\n").split("\n")]
    nonempty = [l for l in lines if l]
    items = [l for l in nonempty if not l.startswith("#")]
    duplicates = len(items) - len(set(items))
    print(f"== {name}: {len(nonempty)} lines, {len(items)} entries, "
          f"dup={duplicates}, BOM={bom}, CRLF={crlf}, LF={lf}")
    if name in ("list-google.txt", "list-exclude-user.txt",
                "list-general-user.txt", "list-general.txt"):
        print(text)
        print()

print("=== compare list-google with upstream ===")
user_google = set(entries(os.path.join(d, "list-google.txt")))
upstream_google = set(entries(os.path.join(up, "list-google.txt")))
print("missing from user list-google:", sorted(upstream_google - user_google))
print("extra in user list-google:", sorted(user_google - upstream_google))

print("=== youtube mentions ===")
for name in sorted(os.listdir(d)):
    text = open(os.path.join(d, name), encoding="utf-8-sig", errors="replace").read().lower()
    hits = [l.strip() for l in text.splitlines()
            if any(k in l for k in ("youtube", "youtu.be", "ytimg", "googlevideo",
                                    "yt3.", "yt4.", "jnn-pa"))]
    if hits:
        print(name, len(hits), hits[:6])

print("=== ipset-all head ===")
text = open(os.path.join(d, "ipset-all.txt"), encoding="utf-8-sig", errors="replace").read()
head = text.replace("\r\n", "\n").split("\n")[:5]
print(head)
print("marker present:", "203.0.113.113/32" in text)
print("empty:", len(text.strip()) == 0)
