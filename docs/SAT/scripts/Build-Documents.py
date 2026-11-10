"""Build the SAT Word documents from docs/SAT/source/*.md.

Outputs: the five documents listed in DOCUMENTS, under docs/SAT.

Requires Python 3.10+ and python-docx. Page numbers are Word fields; run
scripts/Update-WordFields.ps1 afterwards (needs Microsoft Word) to fill in page numbers.

Supported Markdown: front matter (title, english), ## / ### headings, a line that is only
**bold** text (minor heading), paragraphs, "- " bullets, "1. " numbered lines, pipe tables,
``` code blocks, and inline **bold**, `code` and [label](https://...) links.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

from docx import Document
from docx.enum.style import WD_STYLE_TYPE
from docx.enum.table import WD_CELL_VERTICAL_ALIGNMENT, WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK
from docx.opc.constants import RELATIONSHIP_TYPE as RT
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Cm, Pt, RGBColor
from docx.text.paragraph import Paragraph

SAT = Path(__file__).resolve().parents[1]
SOURCE = SAT / "source"
DOCUMENTS = ["Code Review Rules", "Design Principles", "Technical Document", "Framework", "安全檢查摘要"]

FONT = "Microsoft JhengHei"
CODE_FONT = "Consolas"
INK = RGBColor(0x1F, 0x29, 0x33)
HEADING = RGBColor(0, 0, 0)
MUTED = RGBColor(0x5B, 0x67, 0x73)
LINK = "1F5A96"
HEADER_FILL = "DCE3EA"
CODE_FILL = "F3F5F7"
BORDER_COLOR = "B9C4CF"
TEXT_WIDTH_CM = 16.6
# Gap between a table and the paragraph after it, matching the empty paragraph (10.5 pt text,
# 1.2 line spacing, 4 pt after) that used to follow every table: its line is 21.8 pt in Word,
# and Word then leaves the larger of its 4 pt space after and the next paragraph's space before.
# Pages therefore only change where that empty paragraph used to start a page (see separate_tables).
TABLE_SPACER_LINE_PT = 21.8
TABLE_SPACER_AFTER_PT = 4


# --------------------------------------------------------------------------- styles

def set_run_fonts(element, name: str) -> None:
    rpr = element.get_or_add_rPr()
    fonts = rpr.find(qn("w:rFonts"))
    if fonts is None:
        fonts = OxmlElement("w:rFonts")
        rpr.insert(0, fonts)
    for key in list(fonts.attrib):
        if "Theme" in key:
            del fonts.attrib[key]
    for attr in ("w:ascii", "w:hAnsi", "w:eastAsia", "w:cs"):
        fonts.set(qn(attr), name)


def style(doc, name: str, size: float, bold: bool = False, color: RGBColor = INK,
          before: float = 0, after: float = 6, keep: bool = False, spacing: float = 1.2) -> None:
    st = doc.styles[name]
    st.font.size = Pt(size)
    st.font.bold = bold
    st.font.italic = False
    st.font.color.rgb = color
    set_run_fonts(st.element, FONT)
    fmt = st.paragraph_format
    fmt.space_before = Pt(before)
    fmt.space_after = Pt(after)
    fmt.line_spacing = spacing
    fmt.keep_with_next = keep
    for border in st.element.xpath("./w:pPr/w:pBdr"):
        border.getparent().remove(border)


def setup(doc) -> None:
    section = doc.sections[0]
    section.page_width, section.page_height = Cm(21.0), Cm(29.7)
    section.top_margin = section.bottom_margin = Cm(2.2)
    section.left_margin = section.right_margin = Cm(2.2)
    section.header_distance = section.footer_distance = Cm(1.0)
    style(doc, "Normal", 10.5, after=6, spacing=1.2)
    style(doc, "Title", 22, True, HEADING, after=4, spacing=1.1)
    style(doc, "Heading 1", 14, True, HEADING, before=18, after=8, keep=True, spacing=1.15)
    style(doc, "Heading 2", 12, True, HEADING, before=12, after=6, keep=True, spacing=1.15)
    style(doc, "Heading 3", 10.5, True, INK, before=8, after=4, keep=True, spacing=1.15)
    style(doc, "List Bullet", 10.5, after=3, spacing=1.2)
    names = {s.name for s in doc.styles}
    for level in (1, 2):
        name = f"TOC {level}"
        toc = doc.styles[name] if name in names else doc.styles.add_style(name, WD_STYLE_TYPE.PARAGRAPH)
        toc.base_style = doc.styles["Normal"]
        toc.font.size = Pt(10.5)
        set_run_fonts(toc.element, FONT)
        fmt = toc.paragraph_format
        fmt.space_before = Pt(0)
        fmt.space_after = Pt(3)
        fmt.line_spacing = 1.1
        fmt.left_indent = Cm(0 if level == 1 else 0.75)


# --------------------------------------------------------------------------- inline text

INLINE = re.compile(r"\[([^\]]+)\]\(([^)]+)\)|`([^`]+)`|\*\*([^*]+)\*\*")


def add_hyperlink(paragraph, label: str, url: str, size: float | None) -> None:
    rel = paragraph.part.relate_to(url, RT.HYPERLINK, is_external=True)
    link = OxmlElement("w:hyperlink")
    link.set(qn("r:id"), rel)
    run = OxmlElement("w:r")
    rpr = OxmlElement("w:rPr")
    color = OxmlElement("w:color")
    color.set(qn("w:val"), LINK)
    underline = OxmlElement("w:u")
    underline.set(qn("w:val"), "single")
    rpr.append(color)
    rpr.append(underline)
    if size:
        sz = OxmlElement("w:sz")
        sz.set(qn("w:val"), str(int(size * 2)))
        rpr.append(sz)
    run.append(rpr)
    text = OxmlElement("w:t")
    text.text = label
    text.set(qn("xml:space"), "preserve")
    run.append(text)
    link.append(run)
    paragraph._p.append(link)


def inline(paragraph, text: str, size: float | None = None, bold: bool = False) -> None:
    position = 0
    for match in INLINE.finditer(text):
        if match.start() > position:
            run = paragraph.add_run(text[position:match.start()])
            run.bold = bold or None
            if size:
                run.font.size = Pt(size)
        label, target, code, strong = match.groups()
        if label is not None:
            target = target.strip("<>")
            if target.startswith("http"):
                add_hyperlink(paragraph, label, target, size)
            else:
                run = paragraph.add_run(label)
                if size:
                    run.font.size = Pt(size)
        elif code is not None:
            run = paragraph.add_run(code)
            run.font.name = CODE_FONT
            set_run_fonts(run._element, CODE_FONT)
            run.font.size = Pt((size or 10.5) - 1)
        else:
            run = paragraph.add_run(strong)
            run.bold = True
            if size:
                run.font.size = Pt(size)
        position = match.end()
    if position < len(text):
        run = paragraph.add_run(text[position:])
        run.bold = bold or None
        if size:
            run.font.size = Pt(size)


# --------------------------------------------------------------------------- blocks

def shade(element, fill: str) -> None:
    pr = element.get_or_add_tcPr() if element.tag.endswith("tc") else element.get_or_add_pPr()
    shd = OxmlElement("w:shd")
    shd.set(qn("w:val"), "clear")
    shd.set(qn("w:color"), "auto")
    shd.set(qn("w:fill"), fill)
    pr.append(shd)


def display_width(text: str) -> float:
    plain = INLINE.sub(lambda m: m.group(1) or m.group(3) or m.group(4) or "", text)
    return sum(2.0 if ord(ch) >= 0x2E80 else 1.0 for ch in plain)


def longest_token(text: str) -> float:
    plain = INLINE.sub(lambda m: m.group(1) or m.group(3) or m.group(4) or "", text)
    tokens = re.split(r"[\s、，；：（）()]+", plain)
    return max((display_width(token) for token in tokens if token), default=0)


def column_widths(rows: list[list[str]], budget: float = TEXT_WIDTH_CM) -> list[float]:
    columns = len(rows[0])
    unit = 0.19  # cm per width unit at table font size
    natural = [max(display_width(row[c]) for row in rows) for c in range(columns)]
    # A column never gets narrower than its longest unbreakable word (capped), so names do not split.
    minimum = [min(max(longest_token(row[c]) for row in rows) * unit + 0.45, 5.6) for c in range(columns)]
    widths = [None] * columns
    fixed = 0.0
    for c in range(columns):
        need = natural[c] * unit + 0.5
        if need <= 3.2:
            widths[c] = max(need, 1.5, minimum[c])
            fixed += widths[c]
    flexible = [c for c in range(columns) if widths[c] is None]
    if flexible:
        remaining = max(budget - fixed, 3.0 * len(flexible))
        weights = [max(natural[c], 8) ** 0.75 for c in flexible]
        for c, weight in zip(flexible, weights):
            widths[c] = remaining * weight / sum(weights)
        # Raise columns below their minimum, taking the difference from the widest flexible column.
        for c in flexible:
            if widths[c] < minimum[c]:
                donor = max(flexible, key=lambda k: widths[k] - minimum[k])
                shift = min(minimum[c] - widths[c], max(widths[donor] - minimum[donor], 0))
                widths[c] += shift
                widths[donor] -= shift
    scale = budget / sum(widths)
    return [w * scale for w in widths]


def table(doc, rows: list[list[str]], header: bool = True) -> None:
    rows = [r for r in rows if not all(re.fullmatch(r":?-{3,}:?", c.strip()) for c in r)]
    columns = len(rows[0])
    rows = [r + [""] * (columns - len(r)) for r in rows]
    widths = column_widths(rows)
    body = rows[1:] if header else rows
    centered = {c for c in range(columns) if body and all(display_width(r[c]) <= 3 for r in body)}
    t = doc.add_table(rows=len(rows), cols=columns)
    t.alignment = WD_TABLE_ALIGNMENT.CENTER
    t.autofit = False
    pr = t._tbl.tblPr
    borders = OxmlElement("w:tblBorders")
    for edge in ("top", "left", "bottom", "right", "insideH", "insideV"):
        b = OxmlElement(f"w:{edge}")
        b.set(qn("w:val"), "single")
        b.set(qn("w:sz"), "4")
        b.set(qn("w:color"), BORDER_COLOR)
        borders.append(b)
    pr.append(borders)
    margins = OxmlElement("w:tblCellMar")
    for edge, value in (("top", 50), ("bottom", 50), ("left", 100), ("right", 100)):
        m = OxmlElement(f"w:{edge}")
        m.set(qn("w:w"), str(value))
        m.set(qn("w:type"), "dxa")
        margins.append(m)
    pr.append(margins)
    for r, values in enumerate(rows):
        row = t.rows[r]
        trpr = row._tr.get_or_add_trPr()
        trpr.append(OxmlElement("w:cantSplit"))
        is_header = header and r == 0
        if is_header:
            trpr.append(OxmlElement("w:tblHeader"))
        for c, value in enumerate(values):
            cell = row.cells[c]
            cell.width = Cm(widths[c])
            cell.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.CENTER if is_header else WD_CELL_VERTICAL_ALIGNMENT.TOP
            label = is_header or (not header and c == 0)
            if label:
                shade(cell._tc, HEADER_FILL)
            paragraph = cell.paragraphs[0]
            fmt = paragraph.paragraph_format
            fmt.space_before = Pt(1)
            fmt.space_after = Pt(1)
            fmt.line_spacing = 1.15
            # Keep the header row (and the first body row) on the same page as the rest of the table.
            fmt.keep_with_next = r < (2 if header else 1)
            if c in centered and not is_header:
                paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
            inline(paragraph, value.strip(), size=9.5, bold=label)
    for c, width in enumerate(widths):
        t.columns[c].width = Cm(width)


def add_table_spacer(table_element) -> None:
    spacer = OxmlElement("w:p")
    table_element.addnext(spacer)
    Paragraph(spacer, None).paragraph_format.space_after = Pt(TABLE_SPACER_AFTER_PT)


def last_row_keeps_with_next(table_element) -> bool:
    last_row = table_element.findall(qn("w:tr"))[-1]
    return bool(Paragraph(last_row.find(".//" + qn("w:p")), None).paragraph_format.keep_with_next)


def separate_tables(doc) -> None:
    """Leave the gap the empty paragraph used to leave after a table, without that empty
    paragraph turning into a blank line at the top of a page.

    The gap goes into the space before of the paragraph that follows the table, which Word
    drops at the top of a page. Three cases keep the empty paragraph:
    - a table followed by another table, which Word would otherwise merge into one;
    - a table at the end of the document, where Word would add a paragraph itself;
    - a table whose last row keeps with next (a header and at most one body row). The empty
      paragraph then moves with the table and never starts a page, so leaving it keeps the
      old page breaks.
    In the first two cases the empty paragraph can still start a page, as before.
    """
    paragraphs = {p._p: p for p in doc.paragraphs}
    for table_element in list(doc.element.body.iterchildren(qn("w:tbl"))):
        following = table_element.getnext()
        if following is None or following.tag != qn("w:p") or last_row_keeps_with_next(table_element):
            add_table_spacer(table_element)
            continue
        paragraph = paragraphs[following]
        before = paragraph.paragraph_format.space_before
        style = paragraph.style
        while before is None and style is not None:
            before = style.paragraph_format.space_before
            style = style.base_style
        own = before.pt if before is not None else 0
        if paragraph.paragraph_format.page_break_before:
            # After the empty paragraph, Word placed a paragraph that forces a new page as if
            # its space before were smaller by the empty paragraph's space after. Set that
            # directly, so no empty paragraph can end up alone on a page before the break.
            paragraph.paragraph_format.space_before = Pt(max(own - TABLE_SPACER_AFTER_PT, 0))
            continue
        paragraph.paragraph_format.space_before = Pt(TABLE_SPACER_LINE_PT + max(TABLE_SPACER_AFTER_PT, own))


def code_block(doc, lines: list[str]) -> None:
    for index, line in enumerate(lines):
        paragraph = doc.add_paragraph()
        fmt = paragraph.paragraph_format
        fmt.left_indent = Cm(0.3)
        fmt.space_before = Pt(4 if index == 0 else 0)
        fmt.space_after = Pt(4 if index == len(lines) - 1 else 0)
        fmt.line_spacing = 1.1
        fmt.keep_with_next = index < len(lines) - 1
        shade(paragraph._p, CODE_FILL)
        run = paragraph.add_run(line if line else " ")
        run.font.size = Pt(8.5)
        run.font.name = CODE_FONT
        set_run_fonts(run._element, CODE_FONT)


def field(paragraph, instruction: str, placeholder: str = "") -> None:
    def fld(kind: str):
        char = OxmlElement("w:fldChar")
        char.set(qn("w:fldCharType"), kind)
        run = OxmlElement("w:r")
        run.append(char)
        return run
    paragraph._p.append(fld("begin"))
    run = OxmlElement("w:r")
    text = OxmlElement("w:instrText")
    text.set(qn("xml:space"), "preserve")
    text.text = f" {instruction} "
    run.append(text)
    paragraph._p.append(run)
    paragraph._p.append(fld("separate"))
    if placeholder:
        paragraph.add_run(placeholder)
    paragraph._p.append(fld("end"))


# --------------------------------------------------------------------------- page furniture

def header_footer(doc, title: str) -> None:
    section = doc.sections[0]
    header = section.header.paragraphs[0]
    header.alignment = WD_ALIGN_PARAGRAPH.RIGHT
    run = header.add_run(f"JET 首次 SAT 評估｜{title}")
    run.font.size = Pt(8.5)
    run.font.color.rgb = MUTED
    footer = section.footer.paragraphs[0]
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER
    for text in ("第 ",):
        run = footer.add_run(text)
        run.font.size = Pt(8.5)
    field(footer, "PAGE", "1")
    run = footer.add_run(" 頁，共 ")
    run.font.size = Pt(8.5)
    field(footer, "NUMPAGES", "1")
    run = footer.add_run(" 頁")
    run.font.size = Pt(8.5)
    for r in footer.runs:
        r.font.size = Pt(8.5)
        r.font.color.rgb = MUTED


def cover(doc, meta: dict) -> None:
    title = doc.add_paragraph(style="Title")
    title.add_run(f"JET {meta['title']}")
    subtitle = doc.add_paragraph()
    run = subtitle.add_run(meta.get("english", ""))
    run.font.size = Pt(12)
    run.font.color.rgb = MUTED
    subtitle.paragraph_format.space_after = Pt(14)
    note = doc.add_paragraph("首次評估版｜草稿，待複核")
    note.paragraph_format.space_after = Pt(12)



# --------------------------------------------------------------------------- build

def parse_front_matter(lines: list[str]) -> tuple[dict, list[str]]:
    meta = {}
    if lines and lines[0].strip() == "---":
        end = lines.index("---", 1)
        for line in lines[1:end]:
            key, _, value = line.partition(":")
            meta[key.strip()] = value.strip()
        lines = lines[end + 1:]
    return meta, lines


def build(name: str) -> None:
    lines = (SOURCE / f"{name}.md").read_text(encoding="utf-8-sig").splitlines()
    meta, lines = parse_front_matter(lines)
    doc = Document()
    setup(doc)
    header_footer(doc, meta["title"])
    cover(doc, meta)

    i = 0
    while i < len(lines):
        line = lines[i].rstrip()
        stripped = line.strip()
        if not stripped:
            i += 1
            continue
        if stripped.startswith("```"):
            block = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                block.append(lines[i].rstrip())
                i += 1
            code_block(doc, block)
            i += 1
            continue
        if stripped.startswith("|"):
            rows = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append([v.strip() for v in lines[i].strip().strip("|").split("|")])
                i += 1
            table(doc, rows)
            continue
        image_match = re.fullmatch(r"!\[([^]]*)\]\(([^)]+)\)", stripped)
        if image_match:
            paragraph = doc.add_paragraph()
            paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
            paragraph.add_run().add_picture(str(SAT / image_match.group(2)), width=Cm(TEXT_WIDTH_CM))
            i += 1
            continue
        if stripped.startswith("### "):
            inline(doc.add_paragraph(style="Heading 2"), stripped[4:])
        elif stripped.startswith("## "):
            paragraph = doc.add_paragraph(style="Heading 1")
            if name == "Framework" and stripped.startswith("## 2."):
                paragraph.paragraph_format.page_break_before = True
                # Word keeps space before after a forced page break, unlike after an automatic one.
                # Drop it so this page starts at the top margin like the others.
                paragraph.paragraph_format.space_before = Pt(0)
            inline(paragraph, stripped[3:])
        elif re.fullmatch(r"\*\*[^*]+\*\*", stripped):
            inline(doc.add_paragraph(style="Heading 3"), stripped[2:-2])
        elif stripped.startswith("- "):
            inline(doc.add_paragraph(style="List Bullet"), stripped[2:])
        elif re.match(r"^\d+\. ", stripped):
            number, _, rest = stripped.partition(" ")
            paragraph = doc.add_paragraph()
            fmt = paragraph.paragraph_format
            fmt.left_indent = Cm(0.75)
            fmt.first_line_indent = Cm(-0.6)
            fmt.space_after = Pt(3)
            paragraph.add_run(number + " ")
            inline(paragraph, rest)
        else:
            paragraph = doc.add_paragraph()
            # A sentence that introduces a table, list or code block stays with it.
            paragraph.paragraph_format.keep_with_next = stripped.endswith("：")
            inline(paragraph, stripped)
        i += 1

    separate_tables(doc)
    props = doc.core_properties
    props.author = "JET"
    props.title = f"JET {meta['title']}"
    props.subject = "首次 SAT 評估"
    props.comments = ""
    out = SAT / f"{name}.docx"
    doc.save(out)
    print(f"saved {out.name}")


if __name__ == "__main__":
    for document in sys.argv[1:] or DOCUMENTS:
        build(document)
