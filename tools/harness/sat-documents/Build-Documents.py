"""Build the SAT Word documents from tools/harness/sat-documents/source/*.md.

Outputs: the documents listed in DOCUMENTS, at the paths given there under docs/SAT.

Usage: Build-Documents.py [--output <folder>] [<source name> ...]
--output writes the documents under that folder instead of docs/SAT, with the same relative
paths. Without source names, every document in DOCUMENTS is built.

Requires Python 3.10+ and python-docx. Page numbers are Word fields; run
Update-WordFields.ps1 afterwards (needs Microsoft Word) to fill in the table of contents and
page numbers. That script also fixes the document properties Word rewrites when it saves.

Front matter: title, english (subtitle), and optionally cover: plain, header (text after the
header prefix), toc: true. cover: plain is for a text that is not ours (the company's review
rules): the title has no JET prefix, there is no version line and no header, and the footer
is the page number alone.

Supported Markdown: ## / ### headings, a line that is only
**bold** text (minor heading), paragraphs, "- " bullets, "1. " numbered lines, pipe tables,
``` code blocks, and inline **bold**, `code` and [label](https://...) links.
"""
from __future__ import annotations

import argparse
import re
from datetime import datetime, timezone
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

SAT = Path(__file__).resolve().parents[3] / "docs" / "SAT"
SOURCE = Path(__file__).resolve().parent / "source"
# Source name under source/ -> output path under docs/SAT. The layout follows the company's SAT folder.
DOCUMENTS = {
    "Technical Document": "Technical Document.docx",
    "Design Principle": "Design Principle.docx",
    "Code review rule": "Code Review/Code review rule.docx",
}

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
IMAGE_MAX_HEIGHT_CM = 21.0
# Gap between a table and the paragraph after it, matching the empty paragraph (10.5 pt text,
# 1.2 line spacing, 4 pt after) that used to follow every table: its line is 21.8 pt in Word,
# and Word then leaves the larger of its 4 pt space after and the next paragraph's space before.
# Pages therefore only change where that empty paragraph used to start a page (see separate_tables).
TABLE_SPACER_LINE_PT = 21.8
TABLE_SPACER_AFTER_PT = 4
# Space after each contents entry. With single line spacing an entry takes about 21 pt, so the
# cover and the 24 entries of the technical document fit on the first page with room for three more.
TOC_SPACE_AFTER_PT = 3
# Gap between a code block and the paragraph after it.
CODE_BLOCK_GAP_PT = 10
# Indent of the wrapped part of a code line that is too long for the page.
CODE_WRAP_INDENT_CM = 0.8
# The elements the schema places after w:overflowPunct inside w:pPr.
PPR_AFTER_OVERFLOW_PUNCT = tuple("w:" + name for name in (
    "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd", "snapToGrid", "spacing",
    "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc", "textDirection",
    "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "rPr", "sectPr", "pPrChange"))


# --------------------------------------------------------------------------- styles

def set_run_fonts(element, name: str, east_asia: str | None = None) -> None:
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
    if east_asia:
        # Consolas has no CJK glyphs; without this Word substitutes a serif face for Chinese in code.
        fonts.set(qn("w:eastAsia"), east_asia)


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


def keep_punctuation_inside(style_element) -> None:
    """Word lets a full-width comma or full stop at the end of a line hang outside the right
    margin, where it sticks out past the edge of the tables. Turn that off."""
    ppr = style_element.get_or_add_pPr()
    for existing in ppr.findall(qn("w:overflowPunct")):
        ppr.remove(existing)
    setting = OxmlElement("w:overflowPunct")
    setting.set(qn("w:val"), "0")
    ppr.insert_element_before(setting, *PPR_AFTER_OVERFLOW_PUNCT)


def setup(doc) -> None:
    section = doc.sections[0]
    section.page_width, section.page_height = Cm(21.0), Cm(29.7)
    section.top_margin = section.bottom_margin = Cm(2.2)
    section.left_margin = section.right_margin = Cm(2.2)
    section.header_distance = section.footer_distance = Cm(1.0)
    style(doc, "Normal", 10.5, after=6, spacing=1.2)
    keep_punctuation_inside(doc.styles["Normal"].element)
    style(doc, "Title", 22, True, HEADING, after=4, spacing=1.1)
    style(doc, "Heading 1", 14, True, HEADING, before=18, after=8, keep=True, spacing=1.15)
    style(doc, "Heading 2", 12, True, HEADING, before=12, after=6, keep=True, spacing=1.15)
    style(doc, "Heading 3", 10.5, True, INK, before=8, after=4, keep=True, spacing=1.15)
    style(doc, "List Bullet", 10.5, after=3, spacing=1.2)
    names = {s.name for s in doc.styles}
    for level in (1, 2):
        # Word only takes these as its contents styles when they carry its own lower-case name
        # and are not marked as custom styles. Otherwise it adds its own "toc 1" when it fills
        # in the contents, and the entries take the spacing of Normal.
        name = f"toc {level}"
        toc = doc.styles[name] if name in names else doc.styles.add_style(
            name, WD_STYLE_TYPE.PARAGRAPH, builtin=True)
        toc.base_style = doc.styles["Normal"]
        toc.font.size = Pt(10.5)
        set_run_fonts(toc.element, FONT)
        fmt = toc.paragraph_format
        fmt.space_before = Pt(0)
        fmt.space_after = Pt(TOC_SPACE_AFTER_PT)
        fmt.line_spacing = 1.0
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
            set_run_fonts(run._element, CODE_FONT, east_asia=FONT)
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


def longest_latin_word(text: str) -> int:
    plain = INLINE.sub(lambda m: m.group(1) or m.group(3) or m.group(4) or "", text)
    return max((len(word) for word in re.findall(r"[!-~]+", plain)), default=0)


def column_widths(rows: list[list[str]], budget: float = TEXT_WIDTH_CM) -> list[float]:
    columns = len(rows[0])
    unit = 0.19  # cm per width unit at table font size
    latin_unit = 0.175  # cm per Latin letter at table font size
    natural = [max(display_width(row[c]) for row in rows) for c in range(columns)]
    # A column never gets narrower than its longest unbreakable word (capped), so names do not split.
    # Word breaks Chinese text anywhere but a Latin word only at a space, so a long package or
    # type name gets its full width, up to a larger cap.
    minimum = [max(min(max(longest_token(row[c]) for row in rows) * unit + 0.45, 5.6),
                   min(max(longest_latin_word(row[c]) for row in rows) * latin_unit + 0.45, 8.0))
               for c in range(columns)]
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
    last = len(rows) - 1
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
            # Keep the header row (and the first body row) on the same page as the rest of the table,
            # and the last three rows together, so a table never carries just one or two rows over.
            fmt.keep_with_next = r < (2 if header else 1) or last - 2 <= r < last
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
        own = space_before_pt(paragraph)
        if paragraph.paragraph_format.page_break_before:
            # After the empty paragraph, Word placed a paragraph that forces a new page as if
            # its space before were smaller by the empty paragraph's space after. Set that
            # directly, so no empty paragraph can end up alone on a page before the break.
            paragraph.paragraph_format.space_before = Pt(max(own - TABLE_SPACER_AFTER_PT, 0))
            continue
        paragraph.paragraph_format.space_before = Pt(TABLE_SPACER_LINE_PT + max(TABLE_SPACER_AFTER_PT, own))


def space_before_pt(paragraph) -> float:
    before = paragraph.paragraph_format.space_before
    style = paragraph.style
    while before is None and style is not None:
        before = style.paragraph_format.space_before
        style = style.base_style
    return before.pt if before is not None else 0


def code_block(doc, lines: list[str]):
    """Returns the last paragraph of the block, or None for an empty block."""
    paragraph = None
    for index, line in enumerate(lines):
        paragraph = doc.add_paragraph()
        fmt = paragraph.paragraph_format
        # A line too long for the page wraps under a hanging indent, so the rest of it does not
        # read as another command.
        fmt.left_indent = Cm(0.3 + CODE_WRAP_INDENT_CM)
        fmt.first_line_indent = Cm(-CODE_WRAP_INDENT_CM)
        fmt.space_before = Pt(4 if index == 0 else 0)
        fmt.space_after = Pt(4 if index == len(lines) - 1 else 0)
        fmt.line_spacing = 1.1
        fmt.keep_with_next = index < len(lines) - 1
        shade(paragraph._p, CODE_FILL)
        run = paragraph.add_run(line if line else " ")
        run.font.size = Pt(8.5)
        run.font.name = CODE_FONT
        set_run_fonts(run._element, CODE_FONT, east_asia=FONT)
    return paragraph


def separate_code_blocks(doc, last_paragraphs: list) -> None:
    """Leave a gap between a code block and the paragraph after it."""
    paragraphs = {p._p: p for p in doc.paragraphs}
    for last in last_paragraphs:
        following = paragraphs.get(last._p.getnext())
        if following is not None:
            following.paragraph_format.space_before = Pt(max(space_before_pt(following), CODE_BLOCK_GAP_PT))


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

def header_footer(doc, meta: dict) -> None:
    section = doc.sections[0]
    footer = section.footer.paragraphs[0]
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER
    if meta.get("cover") == "plain":
        # Not our text: no JET header, and the footer is the page number alone.
        field(footer, "PAGE", "1")
    else:
        header = section.header.paragraphs[0]
        header.alignment = WD_ALIGN_PARAGRAPH.RIGHT
        run = header.add_run(f"JET｜{meta.get('header', meta['title'])}")
        run.font.size = Pt(8.5)
        run.font.color.rgb = MUTED
        footer.add_run("第 ")
        field(footer, "PAGE", "1")
        footer.add_run(" 頁，共 ")
        field(footer, "NUMPAGES", "1")
        footer.add_run(" 頁")
    for r in footer.runs:
        r.font.size = Pt(8.5)
        r.font.color.rgb = MUTED


def cover(doc, meta: dict) -> None:
    plain = meta.get("cover") == "plain"
    title = doc.add_paragraph(style="Title")
    title.add_run(meta["title"] if plain else f"JET {meta['title']}")
    subtitle = doc.add_paragraph()
    run = subtitle.add_run(meta.get("english", ""))
    run.font.size = Pt(12)
    run.font.color.rgb = MUTED
    subtitle.paragraph_format.space_after = Pt(14)
    if not plain:
        note = doc.add_paragraph("版本 1.0.0.0")
        note.paragraph_format.space_after = Pt(12)


def contents(doc, meta: dict):
    """Table of contents as a Word field; Update-WordFields.ps1 fills it in.

    Returns the field's paragraph. The text starts on the page after the contents; build() puts
    that page break on the paragraph that follows, so no empty paragraph has to carry it.
    """
    label = doc.add_paragraph()
    run = label.add_run("Table of Contents" if meta.get("cover") == "plain" else "目錄")
    run.bold = True
    run.font.size = Pt(12)
    label.paragraph_format.space_before = Pt(6)
    entries = doc.add_paragraph()
    field(entries, r'TOC \o "1-2" \h \z \u', "以 Word 更新功能變數後顯示目錄。")
    return entries


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


def next_block_is_table_or_code(lines: list[str], index: int) -> bool:
    for line in lines[index + 1:]:
        if line.strip():
            return line.strip().startswith(("|", "```"))
    return False


def page_break_after(paragraph) -> None:
    """Start a new page after this paragraph. A following paragraph carries the break itself;
    before a table or at the end of the document an empty paragraph has to carry it."""
    following = paragraph._p.getnext()
    if following is not None and following.tag == qn("w:p"):
        Paragraph(following, None).paragraph_format.page_break_before = True
        return
    carrier = OxmlElement("w:p")
    paragraph._p.addnext(carrier)
    Paragraph(carrier, None).add_run().add_break(WD_BREAK.PAGE)


def build(name: str, output: Path) -> None:
    lines = (SOURCE / f"{name}.md").read_text(encoding="utf-8-sig").splitlines()
    meta, lines = parse_front_matter(lines)
    doc = Document()
    setup(doc)
    header_footer(doc, meta)
    cover(doc, meta)
    toc_entries = contents(doc, meta) if meta.get("toc") == "true" else None

    code_block_ends = []
    after_heading = False
    i = 0
    while i < len(lines):
        line = lines[i].rstrip()
        stripped = line.strip()
        if not stripped:
            i += 1
            continue
        follows_heading, after_heading = after_heading, False
        if stripped.startswith("```"):
            block = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                block.append(lines[i].rstrip())
                i += 1
            end = code_block(doc, block)
            if end is not None:
                code_block_ends.append(end)
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
            picture = paragraph.add_run().add_picture(str(SOURCE / image_match.group(2)), width=Cm(TEXT_WIDTH_CM))
            if picture.height > Cm(IMAGE_MAX_HEIGHT_CM):
                picture.width = int(picture.width * Cm(IMAGE_MAX_HEIGHT_CM) / picture.height)
                picture.height = Cm(IMAGE_MAX_HEIGHT_CM)
            i += 1
            continue
        if stripped.startswith("### "):
            inline(doc.add_paragraph(style="Heading 2"), stripped[4:])
            after_heading = True
        elif stripped.startswith("## "):
            inline(doc.add_paragraph(style="Heading 1"), stripped[3:])
            after_heading = True
        elif re.fullmatch(r"\*\*[^*]+\*\*", stripped):
            inline(doc.add_paragraph(style="Heading 3"), stripped[2:-2])
            after_heading = True
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
            # A sentence that introduces a table, list or code block stays with it. So does the
            # one paragraph between a heading and a table or code block, so the heading and that
            # paragraph are not left at the foot of a page.
            paragraph.paragraph_format.keep_with_next = stripped.endswith("：") or (
                follows_heading and next_block_is_table_or_code(lines, i))
            inline(paragraph, stripped)
        i += 1

    if toc_entries is not None:
        page_break_after(toc_entries)
    separate_code_blocks(doc, code_block_ends)
    separate_tables(doc)
    plain = meta.get("cover") == "plain"
    now = datetime.now(timezone.utc)
    props = doc.core_properties
    # Word puts the name of its user into "last modified by" when Update-WordFields.ps1 saves
    # the file; that script sets it back to JET.
    props.author = "JET"
    props.last_modified_by = "JET"
    props.title = meta["title"] if plain else f"JET {meta['title']}"
    props.subject = "" if plain else "JET 1.0.0.0"
    props.comments = ""
    # The template that python-docx starts from carries its own creation date.
    props.created = now
    props.modified = now
    out = output / DOCUMENTS[name]
    out.parent.mkdir(parents=True, exist_ok=True)
    doc.save(out)
    print(f"saved {DOCUMENTS[name]}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Build the SAT Word documents from source/*.md.")
    parser.add_argument("names", nargs="*", metavar="name",
                        help="source name without .md; default: " + ", ".join(DOCUMENTS))
    parser.add_argument("--output", type=Path, default=SAT, metavar="folder",
                        help="folder to write under, with the same relative paths; default: docs/SAT")
    args = parser.parse_args()
    unknown = [name for name in args.names if name not in DOCUMENTS]
    if unknown:
        parser.error("unknown source name: " + ", ".join(unknown))
    output = args.output.resolve()
    if output != SAT:
        print(f"output folder: {output}")
    for name in args.names or DOCUMENTS:
        build(name, output)


if __name__ == "__main__":
    main()
