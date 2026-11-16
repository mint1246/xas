from html import escape
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REPORTS = ROOT / "reports"
OUTPUT = REPORTS / "fix-verification-2026-09-30.html"
CASES_FILE = Path(__file__).with_name("cases.json")
TEMPLATE_FILE = Path(__file__).with_name("fix-template.html")

cases = json.loads(CASES_FILE.read_text(encoding="utf-8"))


def render_case(case):
    case_id, title, change, check = case
    return (
        f'<tr><td><span class="id">{escape(case_id)}</span></td>'
        f"<td><strong>{escape(title)}</strong><p>{escape(change)}</p></td>"
        f"<td>{escape(check)}</td></tr>"
    )


rows = "".join(render_case(case) for case in cases)
log_files = [
    ("final-build.txt", "Final solution build"),
    ("windows-tests.txt", "Windows regression suites"),
    ("windows-integration.txt", "Isolated network integration"),
    ("linux-tests.txt", "Linux regression suites"),
    ("linux-terminal.txt", "Linux PTY input and restoration"),
]
logs = []
for log_name, title in log_files:
    log_path = Path(__file__).parent / log_name
    if not log_path.exists():
        continue
    log = log_path.read_text(encoding="utf-8-sig").replace("\x1b", "\\x1b")
    logs.append(
        f"<details><summary>{escape(title)}</summary>"
        f"<pre>{escape(log)}</pre></details>"
    )

template = TEMPLATE_FILE.read_text(encoding="utf-8")
report = template.replace("__ROWS__", rows).replace("__LOGS__", "".join(logs))
output_path = REPORTS / OUTPUT.name
output_path.write_text(report, encoding="utf-8")
