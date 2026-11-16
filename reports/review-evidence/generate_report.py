from html import escape
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REPORTS = Path(__file__).resolve().parent.parent
OUTPUT = REPORTS / "code-review-2026-09-29.html"
FINDINGS_FILE = Path(__file__).with_name("findings.json")
TEMPLATE_FILE = Path(__file__).with_name("review-template.html")

findings = json.loads(FINDINGS_FILE.read_text(encoding="utf-8"))
findings.sort(key=lambda finding: (finding["p"], int(finding["id"][1:])))


def render_snippet(source_path, first_line, last_line):
    source_lines = (ROOT / source_path).read_text(encoding="utf-8-sig").splitlines()
    excerpt = "\n".join(
        f"{line_number:4}  {source_lines[line_number - 1]}"
        for line_number in range(first_line, min(last_line, len(source_lines)) + 1)
    )
    return (
        f'<div class="source"><a href="../{escape(source_path)}">'
        f"{escape(source_path)}:{first_line}–{last_line}</a>"
        f"<pre><code>{escape(excerpt)}</code></pre></div>"
    )


def render_finding(finding):
    sources = "".join(render_snippet(*source) for source in finding["sources"])
    priority = finding["p"]
    priority_class = priority.lower()
    area = escape(finding["area"])
    finding_id = finding["id"]
    return (
        f'<article id="{finding_id}" data-priority="{priority}" '
        f'data-area="{area}">\n'
        f'      <div class="eyebrow"><span class="badge {priority_class}">'
        f"{priority}</span><span>{finding_id} · {area}</span>"
        f'<span class="verification">{escape(finding["state"])}'
        f"</span></div>\n"
        f"      <h2>{escape(finding['title'])}</h2>"
        f"<p><b>Trigger.</b> {escape(finding['trigger'])}</p>\n"
        f"      <p>{escape(finding['problem'])}</p>"
        f"<p><b>Impact.</b> {escape(finding['impact'])}</p>\n"
        f'      <div class="fix"><b>Suggested fix.</b> '
        f"{escape(finding['fix'])}</div>\n"
        f'      <p class="regression"><b>Regression check.</b> '
        f"{escape(finding['test'])}</p>\n"
        f"      <details><summary>Evidence and source excerpts</summary>"
        f"<p>{escape(finding['evidence'])}</p>{sources}</details></article>"
    )


def render_summary_row(finding):
    priority = finding["p"]
    priority_class = priority.lower()
    area = escape(finding["area"])
    finding_id = finding["id"]
    title = escape(finding["title"])
    return (
        f'<tr data-priority="{priority}"><td><span class="badge {priority_class}">'
        f"{priority}</span></td><td>{area}</td><td><a href=\"#{finding_id}\">"
        f"{finding_id} · {title}</a></td></tr>"
    )


cards = "".join(render_finding(finding) for finding in findings)
rows = "".join(render_summary_row(finding) for finding in findings)
areas = "".join(
    f"<option>{escape(area)}</option>"
    for area in sorted({finding["area"] for finding in findings})
)
template = TEMPLATE_FILE.read_text(encoding="utf-8")
report = template.replace("ROWS", rows).replace("AREAS", areas).replace("CARDS", cards)
OUTPUT.write_text(report, encoding="utf-8")
print(f"Created {OUTPUT} ({OUTPUT.stat().st_size:,} bytes; {len(findings)} findings)")
