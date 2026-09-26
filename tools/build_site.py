"""Build a Pages artifact with content-fingerprinted static assets."""

import hashlib
import shutil
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "site"
OUTPUT = ROOT / "site-dist"
OFFICIAL_CODEC = ROOT / "js" / "fic.js"


def replace_once(content: str, old: str, new: str) -> str:
    if content.count(old) != 1:
        raise ValueError(f"Expected exactly one reference to {old!r}")
    return content.replace(old, new)


def write_fingerprinted(name: str, content: bytes) -> str:
    source = Path(name)
    digest = hashlib.sha256(content).hexdigest()[:12]
    target_name = f"{source.stem}.{digest}{source.suffix}"
    (OUTPUT / target_name).write_bytes(content)
    return target_name


def main() -> None:
    # OUTPUT is a fixed, repository-local build directory.
    if OUTPUT.resolve().parent != ROOT.resolve():
        raise ValueError("Build output must be inside the repository")
    if OUTPUT.exists():
        shutil.rmtree(OUTPUT)
    OUTPUT.mkdir()

    data_name = write_fingerprinted("benchmark-data.json", (SOURCE / "benchmark-data.json").read_bytes())
    codec = OFFICIAL_CODEC.read_bytes()
    if (SOURCE / "fic.js").read_bytes() != codec:
        raise ValueError("site/fic.js differs from the official js/fic.js")
    codec_name = write_fingerprinted("fic.js", codec)
    (OUTPUT / "fic.js").write_bytes(codec)  # Stable URL for the documented import.
    script = (SOURCE / "app.js").read_text(encoding="utf-8")
    script = replace_once(script, "fetch('benchmark-data.json')", f"fetch('{data_name}')")
    script = replace_once(script, "import('./fic.js')", f"import('./{codec_name}')")
    script_name = write_fingerprinted("app.js", script.encode("utf-8"))
    style_name = write_fingerprinted("styles.css", (SOURCE / "styles.css").read_bytes())

    html = (SOURCE / "index.html").read_text(encoding="utf-8")
    html = replace_once(html, 'href="styles.css"', f'href="{style_name}"')
    html = replace_once(html, 'src="app.js"', f'src="{script_name}"')
    (OUTPUT / "index.html").write_text(html, encoding="utf-8")

    for item in SOURCE.iterdir():
        if item.is_file() and item.name not in {"index.html", "app.js", "styles.css", "benchmark-data.json", "fic.js"}:
            shutil.copy2(item, OUTPUT / item.name)

    print(f"Built {OUTPUT}")


if __name__ == "__main__":
    main()
