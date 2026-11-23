import datetime
import os
import shutil

source = r"C:\Users\andre\Desktop\lists"
target = r"D:\PROJECT\OpenCode\Zapret\src\zapret\lists"

domain_files = [
    "list-general.txt",
    "list-general-user.txt",
    "list-google.txt",
    "list-exclude.txt",
    "list-exclude-user.txt",
    "ipset-exclude.txt",
    "ipset-exclude-user.txt",
]


def read_lines(path):
    text = open(path, encoding="utf-8-sig", errors="replace").read()
    return [line.rstrip() for line in text.replace("\r\n", "\n").split("\n")]


def write_lines(path, lines):
    text = "\r\n".join(lines).rstrip("\r\n") + "\r\n"
    open(path, "w", encoding="utf-8", newline="").write(text)


def merge_domains(current_path, source_path):
    current = read_lines(current_path) if os.path.exists(current_path) else []
    extra = read_lines(source_path) if os.path.exists(source_path) else []

    seen_comments = set()
    seen_items = set()
    result = []

    for line in current + extra:
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.startswith("#"):
            if stripped in seen_comments:
                continue
            seen_comments.add(stripped)
            result.append(stripped)
            continue

        key = stripped.lower()
        if key in seen_items:
            continue
        seen_items.add(key)
        result.append(stripped)

    return result, len(seen_items)


def full_ipset(path):
    if not os.path.exists(path):
        return []
    lines = [l.strip() for l in read_lines(path) if l.strip()]
    real = [l for l in lines if not l.startswith("#") and l != "203.0.113.113/32"]
    return real if len(real) > 100 else []


print("Source:", source)
print("Target:", target)

backup = target + ".backup-" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
shutil.copytree(target, backup)
print("Backup:", backup)

for name in domain_files:
    current_path = os.path.join(target, name)
    source_path = os.path.join(source, name)
    before = len([l for l in read_lines(current_path) if l.strip()]) if os.path.exists(current_path) else 0
    merged, count = merge_domains(current_path, source_path)
    write_lines(current_path, merged)
    print(f"{name:24} {before:4} -> {count:4} entries")

current_ipset = full_ipset(os.path.join(target, "ipset-all.txt"))
source_ipset = full_ipset(os.path.join(source, "ipset-all.txt"))
union = []
seen = set()
for line in current_ipset + source_ipset:
    key = line.lower()
    if key in seen:
        continue
    seen.add(key)
    union.append(line)

if len(union) > 100:
    write_lines(os.path.join(target, "ipset-all.txt"), union)
    write_lines(os.path.join(target, "ipset-all.txt.backup"), union)
    print(f"ipset-all.txt            {len(current_ipset):5} + {len(source_ipset):5} -> {len(union):5} entries (loaded)")
else:
    print("ipset-all.txt            no full list found, mode left as is")

print("Done.")
