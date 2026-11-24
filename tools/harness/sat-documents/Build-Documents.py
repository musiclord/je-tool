"""Build the SAT Word documents from tools/harness/sat-documents/source/*.md.

Outputs: the documents listed in DOCUMENTS, at the paths given there under docs/SAT.

Usage: Build-Documents.py [--output <folder>] [<source name> ...]
--output writes the documents under that folder instead of docs/SAT, with the same relative
paths. Without source names, every document in DOCUMENTS is built.

Requires Python 3.10+, python-docx and lxml. The table of contents is a Word field; run
Update-WordFields.ps1 afterwards (needs Microsoft Word) to fill it in. That script also fixes
the document properties Word rewrites when it saves.

Page layout and styles follow the company's SAT templates. The values are written out below;
the template files themselves are not copied into the repository:
- A4 portrait, 2.54 cm margins, a line grid of 18 pt, no header and no footer.
- Office theme fonts: Aptos for text, Aptos Display for titles and headings, 新細明體 for
  Chinese; document defaults of 12 pt with 8 pt after each paragraph and 1.16 line spacing.
- The template's styles: Title, Subtitle, Heading 1 to 3, toc 1 and 2, List Paragraph, and the
  two body styles 內文1 and 內文2. Headings use Aptos, as the template's headings do.
- The cover is the title, an optional subtitle and a centred bold "Table of Contents" above the
  contents field TOC \\o "1-2" \\h \\* MERGEFORMAT. The text starts on the next page.
Tables, code blocks and captions use the same theme fonts at a smaller size, and do not snap
to the line grid, so they stay compact.

Front matter: title (the cover title and the title in the file properties), and optionally
subtitle, body (the style of plain paragraphs; default Normal), toc: true, and cover: plain
or cover: rules. cover: plain marks a text that is not ours (the company's review rules): its
contents label uses the company document's spacing and its file properties carry no product
subject. cover: rules is our own rules written in the same layout as the company's review rules:
the same contents label, and the product subject in the file properties.

Supported Markdown: ## / ### headings, a line that is only **bold** text (Heading 3),
paragraphs, "- " bullets, "1. " numbered lines, pipe tables, ``` code blocks, ![name](file)
images, and inline **bold**, `code` and [label](https://...) links.
A paragraph that starts with {style name} and a space takes that style instead of the body
style; the technical document uses it for the 內文1 paragraphs of section 5.
<br> starts a new line: inside a paragraph it is a line break in the same paragraph; in a table
cell each line becomes its own paragraph in the cell.
A "- " bullet indented by two or more spaces is a second-level bullet under the bullet before it.
Bullets and numbered lines use List Paragraph with Word numbering, as the company's review
rules do. A run of numbered lines becomes one Word numbered list that starts at 1; the numbers
come from Word, so the paragraph text does not contain them. The name of an image is its
alternative text and the caption under it.
"""
from __future__ import annotations

import argparse
import re
from datetime import datetime, timezone
from pathlib import Path

from docx import Document
from docx.enum.style import WD_STYLE_TYPE
from docx.enum.table import WD_CELL_VERTICAL_ALIGNMENT, WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.opc.constants import RELATIONSHIP_TYPE as RT
from docx.oxml import OxmlElement, parse_xml
from docx.oxml.ns import nsdecls, qn
from docx.shared import Cm, Pt, RGBColor, Twips
from docx.text.paragraph import Paragraph
from lxml import etree

SAT = Path(__file__).resolve().parents[3] / "docs" / "SAT"
SOURCE = Path(__file__).resolve().parent / "source"
# Source name under source/ -> output path under docs/SAT. The layout follows the company's SAT folder.
DOCUMENTS = {
    "Technical Document": "Technical Document.docx",
    "Design Principle": "Design Principle.docx",
    "Code review rule": "Code Review/Code review rule.docx",
    "Unit test rule": "Code Review/Unit test rule.docx",
}

# Product and version for the subject in the file properties of our own documents.
PRODUCT = "JE Tool"
VERSION = "1.0.0.0"

# Page: A4 with 2.54 cm margins and an 18 pt line grid, as in the templates.
PAGE_WIDTH_TWIPS, PAGE_HEIGHT_TWIPS = 11906, 16838
MARGIN_TWIPS = 1440
HEADER_FOOTER_DISTANCE_TWIPS = 720
LINE_PITCH_TWIPS = 360
TEXT_WIDTH_CM = (PAGE_WIDTH_TWIPS - 2 * MARGIN_TWIPS) / 567
IMAGE_MAX_HEIGHT_CM = 20.0

# Theme: the Office theme of the templates (fonts and colours). The theme keeps its name,
# Office Theme; only these values change.
THEME_FONTS = {"majorFont": "Aptos Display", "minorFont": "Aptos"}
THEME_CHINESE_FONT = "新細明體"
THEME_COLORS = {
    "dk2": "0E2841", "lt2": "E8E8E8", "accent1": "156082", "accent2": "E97132",
    "accent3": "196B24", "accent4": "0F9ED5", "accent5": "A02B93", "accent6": "4EA72E",
    "hlink": "467886", "folHlink": "96607D",
}

THEME_TEXT_FONTS = ('<w:rFonts w:asciiTheme="minorHAnsi" w:eastAsiaTheme="minorEastAsia" '
                    'w:hAnsiTheme="minorHAnsi" w:cstheme="minorBidi"/>')
THEME_HEADING_FONTS = ('<w:rFonts w:asciiTheme="majorHAnsi" w:eastAsiaTheme="majorEastAsia" '
                       'w:hAnsiTheme="majorHAnsi" w:cstheme="majorBidi"/>')
HEADING_COLOR = '<w:color w:val="0F4761" w:themeColor="accent1" w:themeShade="BF"/>'
BODY_FONTS = '<w:rFonts w:ascii="Aptos" w:eastAsia="新細明體" w:hAnsi="Aptos" w:cs="新細明體"/>'

DOC_DEFAULTS = (
    f'<w:docDefaults {nsdecls("w")}><w:rPrDefault><w:rPr>{THEME_TEXT_FONTS}<w:kern w:val="2"/>'
    '<w:sz w:val="24"/><w:szCs w:val="24"/><w:lang w:val="en-US" w:eastAsia="zh-TW" w:bidi="ar-SA"/>'
    '</w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="278" '
    'w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>')

# Paragraph and run properties of the template styles, by style name.
STYLE_FORMATS = {
    "Normal": ('<w:pPr><w:widowControl w:val="0"/></w:pPr>', None),
    "Heading 1": ('<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="480" w:after="80"/>'
                  '<w:outlineLvl w:val="0"/></w:pPr>',
                  f'<w:rPr>{THEME_HEADING_FONTS}{HEADING_COLOR}<w:sz w:val="48"/><w:szCs w:val="48"/></w:rPr>'),
    "Heading 2": ('<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="160" w:after="80"/>'
                  '<w:outlineLvl w:val="1"/></w:pPr>',
                  f'<w:rPr>{THEME_HEADING_FONTS}{HEADING_COLOR}<w:sz w:val="40"/><w:szCs w:val="40"/></w:rPr>'),
    "Heading 3": ('<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="160" w:after="40"/>'
                  '<w:outlineLvl w:val="2"/></w:pPr>',
                  '<w:rPr><w:rFonts w:eastAsiaTheme="majorEastAsia" w:cstheme="majorBidi"/>'
                  f'{HEADING_COLOR}<w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr>'),
    "Title": ('<w:pPr><w:spacing w:after="80" w:line="240" w:lineRule="auto"/><w:contextualSpacing/>'
              '<w:jc w:val="center"/></w:pPr>',
              f'<w:rPr>{THEME_HEADING_FONTS}<w:spacing w:val="-10"/><w:kern w:val="28"/>'
              '<w:sz w:val="56"/><w:szCs w:val="56"/></w:rPr>'),
    "Subtitle": ('<w:pPr><w:jc w:val="center"/></w:pPr>',
                 f'<w:rPr>{THEME_HEADING_FONTS}<w:color w:val="595959" w:themeColor="text1" w:themeTint="A6"/>'
                 '<w:spacing w:val="15"/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr>'),
    "List Paragraph": ('<w:pPr><w:ind w:left="720"/><w:contextualSpacing/></w:pPr>', None),
    "內文1": ('<w:pPr><w:widowControl/><w:spacing w:line="300" w:lineRule="atLeast"/></w:pPr>',
            f'<w:rPr>{BODY_FONTS}<w:sz w:val="22"/></w:rPr>'),
    "內文2": ('<w:pPr><w:widowControl/><w:spacing w:line="276" w:lineRule="auto"/></w:pPr>',
            f'<w:rPr>{BODY_FONTS}</w:rPr>'),
    "toc 1": (None, None),
    "toc 2": ('<w:pPr><w:ind w:leftChars="200" w:left="480"/></w:pPr>', None),
}
# Styles the python-docx starting document does not have. Word only takes "toc 1" and "toc 2"
# as its contents styles when they carry its own lower-case name and are not custom styles.
ADDED_STYLES = {"內文1": False, "內文2": False, "toc 1": True, "toc 2": True}

# The compatibility options of the templates (Word 2013 and later, East Asian layout).
COMPAT = (
    f'<w:compat {nsdecls("w")}><w:spaceForUL/><w:balanceSingleByteDoubleByteWidth/>'
    '<w:doNotLeaveBackslashAlone/><w:ulTrailSpace/><w:doNotExpandShiftReturn/>'
    '<w:adjustLineHeightInTable/><w:useFELayout/>'
    + "".join(f'<w:compatSetting w:name="{name}" w:uri="http://schemas.microsoft.com/office/word" w:val="{value}"/>'
              for name, value in (("compatibilityMode", 15), ("overrideTableStyleFontSizeAndJustification", 1),
                                  ("enableOpenTypeFeatures", 1), ("doNotFlipMirrorIndents", 1),
                                  ("differentiateMultirowTableHeaders", 1),
                                  ("useWord2013TrackBottomHyphenation", 0)))
    + '</w:compat>')

# Lists, as in the company's review rules: a Symbol bullet or "1." at 1.27 cm with a 0.63 cm
# hanging indent. The second bullet level is Word's own "o" one step further in.
BULLET_LEVELS = (
    '<w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val=""/>'
    '<w:lvlJc w:val="left"/><w:pPr><w:tabs><w:tab w:val="num" w:pos="720"/></w:tabs>'
    '<w:ind w:left="720" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Symbol" w:hAnsi="Symbol" '
    'w:hint="default"/><w:sz w:val="20"/></w:rPr></w:lvl>'
    '<w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="o"/>'
    '<w:lvlJc w:val="left"/><w:pPr><w:tabs><w:tab w:val="num" w:pos="1440"/></w:tabs>'
    '<w:ind w:left="1440" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Courier New" '
    'w:hAnsi="Courier New" w:cs="Courier New" w:hint="default"/></w:rPr></w:lvl>')
NUMBER_LEVELS = (
    '<w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/>'
    '<w:lvlJc w:val="left"/><w:pPr><w:tabs><w:tab w:val="num" w:pos="720"/></w:tabs>'
    '<w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>')

TABLE_FONT_PT = 10
CODE_FONT_PT = 10
CAPTION_FONT_PT = 10
CAPTION_COLOR = RGBColor(0x59, 0x59, 0x59)
LINK = "467886"
HEADER_FILL = "DCE3EA"
CODE_FILL = "F3F5F7"
BORDER_COLOR = "B9C4CF"
# Gap between a table and the paragraph after it, and the height of the empty paragraph that
# keeps two tables in a row apart.
TABLE_GAP_PT = 12
# Gap between a code block and the paragraph after it.
CODE_BLOCK_GAP_PT = 10
# Indent of the wrapped part of a code line that is too long for the page.
CODE_WRAP_INDENT_CM = 0.8

# The elements the schema places after a given element inside w:pPr, w:tcPr and w:tblPr.
PPR_ORDER = ("pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr",
             "suppressLineNumbers", "pBdr", "shd", "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap",
             "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd",
             "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc",
             "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle",
             "rPr", "sectPr", "pPrChange")
TCPR_ORDER = ("cnfStyle", "tcW", "gridSpan", "hMerge", "vMerge", "tcBorders", "shd", "noWrap", "tcMar",
              "textDirection", "tcFitText", "vAlign", "hideMark", "headers", "cellIns", "cellDel",
              "cellMerge", "tcPrChange")
TBLPR_ORDER = ("tblStyle", "tblpPr", "tblOverlap", "bidiVisual", "tblStyleRowBandSize",
               "tblStyleColBandSize", "tblW", "jc", "tblCellSpacing", "tblInd", "tblBorders", "shd",
               "tblLayout", "tblCellMar", "tblLook", "tblCaption", "tblDescription", "tblPrChange")


def insert_in_order(parent, child, order: tuple[str, ...]) -> None:
    """Put child into parent where the schema wants it, replacing an element of the same kind."""
    name = child.tag.split("}")[1]
    for existing in parent.findall(qn("w:" + name)):
        parent.remove(existing)
    successors = order[order.index(name) + 1:]
    parent.insert_element_before(child, *("w:" + tag for tag in successors))


def flag(name: str, value: str | None = None):
    element = OxmlElement("w:" + name)
    if value is not None:
        element.set(qn("w:val"), value)
    return element


# --------------------------------------------------------------------------- styles

def set_theme(doc) -> None:
    part = next(rel.target_part for rel in doc.part.rels.values() if rel.reltype == RT.THEME)
    root = etree.fromstring(part.blob)
    ns = {"a": "http://schemas.openxmlformats.org/drawingml/2006/main"}
    if root.get("name") != "Office Theme":
        raise SystemExit("the starting document's theme is not Office Theme")
    for kind, face in THEME_FONTS.items():
        font = root.find(f".//a:fontScheme/a:{kind}", ns)
        font.find("a:latin", ns).set("typeface", face)
        chinese = [f for f in font.findall("a:font", ns) if f.get("script") == "Hant"]
        if len(chinese) != 1:
            raise SystemExit(f"the theme's {kind} has no single Traditional Chinese font")
        chinese[0].set("typeface", THEME_CHINESE_FONT)
    for name, value in THEME_COLORS.items():
        root.find(f".//a:clrScheme/a:{name}/a:srgbClr", ns).set("val", value)
    part._blob = etree.tostring(root, xml_declaration=True, encoding="UTF-8", standalone=True)


def set_settings(doc) -> None:
    settings = doc.settings.element
    language = settings.find(qn("w:themeFontLang"))
    language.set(qn("w:val"), "en-US")
    language.set(qn("w:eastAsia"), "zh-TW")
    old = settings.find(qn("w:compat"))
    old.addprevious(parse_xml(COMPAT))
    settings.remove(old)


def set_style_format(style_element, ppr: str | None, rpr: str | None) -> None:
    for tag in ("w:pPr", "w:rPr"):
        for existing in style_element.findall(qn(tag)):
            style_element.remove(existing)
    for xml in (ppr, rpr):
        if xml:
            style_element.append(parse_xml(xml.replace(">", f" {nsdecls('w')}>", 1)))


def setup(doc) -> None:
    section = doc.sections[0]
    section.page_width, section.page_height = Twips(PAGE_WIDTH_TWIPS), Twips(PAGE_HEIGHT_TWIPS)
    section.top_margin = section.bottom_margin = Twips(MARGIN_TWIPS)
    section.left_margin = section.right_margin = Twips(MARGIN_TWIPS)
    section.header_distance = section.footer_distance = Twips(HEADER_FOOTER_DISTANCE_TWIPS)
    section.gutter = Twips(0)
    grid = section._sectPr.find(qn("w:docGrid"))
    grid.set(qn("w:type"), "lines")
    grid.set(qn("w:linePitch"), str(LINE_PITCH_TWIPS))

    set_theme(doc)
    set_settings(doc)
    styles = doc.styles.element
    old = styles.find(qn("w:docDefaults"))
    old.addprevious(parse_xml(DOC_DEFAULTS))
    styles.remove(old)

    names = {s.name for s in doc.styles}
    for name, builtin in ADDED_STYLES.items():
        if name not in names:
            doc.styles.add_style(name, WD_STYLE_TYPE.PARAGRAPH, builtin=builtin)
    for name, (ppr, rpr) in STYLE_FORMATS.items():
        st = doc.styles[name]
        if name != "Normal":
            st.base_style = doc.styles["Normal"]
        if name.startswith("toc "):
            st.next_paragraph_style = doc.styles["Normal"]
            st.priority = 39
            st.unhide_when_used = True
        set_style_format(st.element, ppr, rpr)
        # Keep the character style Word links to a heading or title in step with it.
        link = st.element.find(qn("w:link"))
        if link is not None and rpr:
            for linked in styles.xpath(f"w:style[@w:styleId='{link.get(qn('w:val'))}']"):
                set_style_format(linked, None, rpr)


# --------------------------------------------------------------------------- lists

class Lists:
    """Word numbering for bullets and numbered lines. Every list gets its own numbering
    instance, so a numbered list always starts at 1."""

    def __init__(self, doc) -> None:
        self.numbering = doc.part.numbering_part.element
        self.bullet = self._abstract(BULLET_LEVELS)
        self.number = self._abstract(NUMBER_LEVELS)

    def _abstract(self, levels: str) -> int:
        ids = [int(a.get(qn("w:abstractNumId"))) for a in self.numbering.findall(qn("w:abstractNum"))]
        new_id = max(ids, default=-1) + 1
        element = parse_xml(f'<w:abstractNum {nsdecls("w")} w:abstractNumId="{new_id}">'
                            f'<w:multiLevelType w:val="hybridMultilevel"/>{levels}</w:abstractNum>')
        first_num = self.numbering.find(qn("w:num"))
        if first_num is not None:
            first_num.addprevious(element)
        else:
            self.numbering.append(element)
        return new_id

    def new(self, numbered: bool) -> int:
        num = self.numbering.add_num(self.number if numbered else self.bullet)
        if numbered:
            num.add_lvlOverride(ilvl=0).add_startOverride(1)
        return num.numId


def list_item(doc, num_id: int, level: int):
    paragraph = doc.add_paragraph(style="List Paragraph")
    num_pr = paragraph._p.get_or_add_pPr().get_or_add_numPr()
    num_pr.get_or_add_ilvl().val = level
    num_pr.get_or_add_numId().val = num_id
    return paragraph


# --------------------------------------------------------------------------- inline text

INLINE = re.compile(r"\[([^\]]+)\]\(([^)]+)\)|`([^`]+)`|\*\*([^*]+)\*\*")
LINE_BREAK = "<br>"


def add_hyperlink(paragraph, label: str, url: str, size: float | None) -> None:
    rel = paragraph.part.relate_to(url, RT.HYPERLINK, is_external=True)
    link = OxmlElement("w:hyperlink")
    link.set(qn("r:id"), rel)
    run = OxmlElement("w:r")
    rpr = OxmlElement("w:rPr")
    color = OxmlElement("w:color")
    color.set(qn("w:val"), LINK)
    color.set(qn("w:themeColor"), "hyperlink")
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
    """Add text with inline bold, code and links; <br> becomes a line break in the paragraph."""
    for index, segment in enumerate(text.split(LINE_BREAK)):
        if index:
            paragraph.add_run().add_break()
        inline_segment(paragraph, segment.strip() if index else segment, size, bold)


def inline_segment(paragraph, text: str, size: float | None, bold: bool) -> None:
    def plain(value: str, strong: bool = False) -> None:
        run = paragraph.add_run(value)
        run.bold = (bold or strong) or None
        if size:
            run.font.size = Pt(size)

    position = 0
    for match in INLINE.finditer(text):
        if match.start() > position:
            plain(text[position:match.start()])
        label, target, code, strong = match.groups()
        if label is not None:
            target = target.strip("<>")
            if target.startswith("http"):
                add_hyperlink(paragraph, label, target, size)
            else:
                plain(label)
        elif code is not None:
            # Code in running text keeps the text fonts; it is written as code in the source only
            # so that nothing in it is read as Markdown.
            plain(code)
        else:
            plain(strong, strong=True)
        position = match.end()
    if position < len(text):
        plain(text[position:])


def use_text_fonts(paragraph) -> None:
    """Headings in the templates set their runs to the text font, Aptos, over the style's
    Aptos Display."""
    for run in paragraph.runs:
        rpr = run._r.get_or_add_rPr()
        for existing in rpr.findall(qn("w:rFonts")):
            rpr.remove(existing)
        rpr.insert(0, parse_xml(THEME_TEXT_FONTS.replace("/>", f" {nsdecls('w')}/>")))


# --------------------------------------------------------------------------- blocks

def off_grid(paragraph) -> None:
    """Keep a table, code or caption line at its own height instead of the 18 pt line grid."""
    insert_in_order(paragraph._p.get_or_add_pPr(), flag("snapToGrid", "0"), PPR_ORDER)


def keep_punctuation_inside(paragraph) -> None:
    """Word lets a full-width comma or full stop at the end of a line hang outside the right
    margin, where it sticks out past the edge of a table cell. Turn that off in cells."""
    insert_in_order(paragraph._p.get_or_add_pPr(), flag("overflowPunct", "0"), PPR_ORDER)


def shade(element, fill: str) -> None:
    shd = OxmlElement("w:shd")
    shd.set(qn("w:val"), "clear")
    shd.set(qn("w:color"), "auto")
    shd.set(qn("w:fill"), fill)
    if element.tag == qn("w:tc"):
        insert_in_order(element.get_or_add_tcPr(), shd, TCPR_ORDER)
    else:
        insert_in_order(element.get_or_add_pPr(), shd, PPR_ORDER)


def display_width(text: str) -> float:
    """Width of the longest line of a cell; lines are separated by <br>."""
    def width(line: str) -> float:
        plain = INLINE.sub(lambda m: m.group(1) or m.group(3) or m.group(4) or "", line)
        return sum(2.0 if ord(ch) >= 0x2E80 else 1.0 for ch in plain)
    return max(width(line) for line in text.split(LINE_BREAK))


def longest_token(text: str) -> float:
    plain = INLINE.sub(lambda m: m.group(1) or m.group(3) or m.group(4) or "", text.replace(LINE_BREAK, " "))
    tokens = re.split(r"[\s、，；：（）()]+", plain)
    return max((display_width(token) for token in tokens if token), default=0)


def longest_latin_word(text: str) -> int:
    plain = INLINE.sub(lambda m: m.group(1) or m.group(3) or m.group(4) or "", text.replace(LINE_BREAK, " "))
    return max((len(word) for word in re.findall(r"[!-~]+", plain)), default=0)


def column_widths(rows: list[list[str]], budget: float = TEXT_WIDTH_CM) -> list[float]:
    columns = len(rows[0])
    unit = 0.2  # cm per width unit (half a Chinese character) at the table font size
    latin_unit = 0.165  # cm per Latin letter at the table font size, measured on a rendered page
    natural = [max(display_width(row[c]) for row in rows) for c in range(columns)]
    # A column never gets narrower than its longest unbreakable word (capped), so names do not split.
    # Word breaks Chinese text anywhere but a Latin word only at a space, so a long package or
    # type name gets its full width, up to a larger cap.
    minimum = [max(min(max(longest_token(row[c]) for row in rows) * unit + 0.45, 5.6),
                   min(max(longest_latin_word(row[c]) for row in rows) * latin_unit + 0.45, 7.2))
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
    insert_in_order(pr, borders, TBLPR_ORDER)
    margins = OxmlElement("w:tblCellMar")
    for edge, value in (("top", 50), ("left", 100), ("bottom", 50), ("right", 100)):
        m = OxmlElement(f"w:{edge}")
        m.set(qn("w:w"), str(value))
        m.set(qn("w:type"), "dxa")
        margins.append(m)
    insert_in_order(pr, margins, TBLPR_ORDER)
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
            label = is_header or (not header and c == 0)
            if label:
                shade(cell._tc, HEADER_FILL)
            cell.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.CENTER if is_header else WD_CELL_VERTICAL_ALIGNMENT.TOP
            for index, line in enumerate(value.strip().split(LINE_BREAK)):
                paragraph = cell.paragraphs[0] if index == 0 else cell.add_paragraph()
                fmt = paragraph.paragraph_format
                fmt.space_before = Pt(1)
                fmt.space_after = Pt(1)
                fmt.line_spacing = 1.15
                # Keep the header row (and the first body row) on the same page as the rest of the table,
                # and the last three rows together, so a table never carries just one or two rows over.
                fmt.keep_with_next = r < (2 if header else 1) or last - 2 <= r < last
                off_grid(paragraph)
                keep_punctuation_inside(paragraph)
                if c in centered and not is_header:
                    paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
                inline(paragraph, line.strip(), size=TABLE_FONT_PT, bold=label)
    for c, width in enumerate(widths):
        t.columns[c].width = Cm(width)


def add_table_spacer(table_element) -> None:
    """An empty paragraph after a table that is followed by another table (Word would otherwise
    merge the two) or that ends the document (Word needs a paragraph there)."""
    spacer = OxmlElement("w:p")
    table_element.addnext(spacer)
    paragraph = Paragraph(spacer, None)
    fmt = paragraph.paragraph_format
    fmt.space_before = Pt(0)
    fmt.space_after = Pt(0)
    fmt.line_spacing = Pt(TABLE_GAP_PT)
    off_grid(paragraph)


def space_before_pt(paragraph) -> float:
    before = paragraph.paragraph_format.space_before
    style = paragraph.style
    while before is None and style is not None:
        before = style.paragraph_format.space_before
        style = style.base_style
    return before.pt if before is not None else 0


def separate_tables(doc) -> None:
    """Leave TABLE_GAP_PT between a table and the paragraph after it, as space before that
    paragraph, which Word drops at the top of a page."""
    paragraphs = {p._p: p for p in doc.paragraphs}
    for table_element in list(doc.element.body.iterchildren(qn("w:tbl"))):
        following = table_element.getnext()
        if following is None or following.tag != qn("w:p"):
            add_table_spacer(table_element)
            continue
        paragraph = paragraphs[following]
        if paragraph.paragraph_format.page_break_before:
            continue
        paragraph.paragraph_format.space_before = Pt(max(space_before_pt(paragraph), TABLE_GAP_PT))


def figure(doc, name: str, path: Path) -> None:
    """A centred picture with its name as alternative text and as the caption below it."""
    paragraph = doc.add_paragraph()
    paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
    paragraph.paragraph_format.keep_with_next = True
    off_grid(paragraph)
    picture = paragraph.add_run().add_picture(str(path), width=Cm(TEXT_WIDTH_CM))
    if picture.height > Cm(IMAGE_MAX_HEIGHT_CM):
        picture.width = int(picture.width * Cm(IMAGE_MAX_HEIGHT_CM) / picture.height)
        picture.height = Cm(IMAGE_MAX_HEIGHT_CM)
    properties = picture._inline.docPr
    properties.set("name", name)
    properties.set("descr", name)
    caption = doc.add_paragraph()
    caption.alignment = WD_ALIGN_PARAGRAPH.CENTER
    off_grid(caption)
    run = caption.add_run(name)
    run.font.size = Pt(CAPTION_FONT_PT)
    run.font.color.rgb = CAPTION_COLOR


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
        off_grid(paragraph)
        shade(paragraph._p, CODE_FILL)
        run = paragraph.add_run(line if line else " ")
        run.font.size = Pt(CODE_FONT_PT)
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


# --------------------------------------------------------------------------- cover

def cover(doc, meta: dict) -> None:
    doc.add_paragraph(meta["title"], style="Title")
    if meta.get("subtitle"):
        subtitle = doc.add_paragraph(style="Subtitle")
        run = subtitle.add_run(meta["subtitle"])
        run.font.size = Pt(12)
        run.font.color.rgb = RGBColor(0x59, 0x59, 0x59)


def contents(doc, meta: dict, body_style: str):
    """The "Table of Contents" label and the contents field; Update-WordFields.ps1 fills it in.

    The text starts on the page after the contents. When Word fills in the contents, it adds a
    paragraph for each entry and leaves the properties of this paragraph on the last one, which
    keeps only the end of the field. That paragraph is 1 pt high, as in the template, and starts
    the next page, so the first heading follows it on that page whatever the length of the
    contents.
    """
    plain = meta.get("cover") in ("plain", "rules")
    label = doc.add_paragraph(style="Normal" if plain else body_style)
    label.alignment = WD_ALIGN_PARAGRAPH.CENTER
    if plain:
        # The spacing of the label in the company's review rules.
        fmt = label.paragraph_format
        fmt.space_before = Pt(12)
        fmt.space_after = Pt(18)
        fmt.line_spacing = 1.15
    run = label.add_run("Table of Contents")
    run.bold = True
    if plain:
        run.font.size = Pt(12)
    entries = doc.add_paragraph()
    field(entries, r'TOC \o "1-2" \h \* MERGEFORMAT', "Update the field to show the table of contents.")
    fmt = entries.paragraph_format
    fmt.page_break_before = True
    fmt.space_before = Pt(0)
    fmt.space_after = Pt(0)
    fmt.line_spacing = 1.0
    off_grid(entries)
    mark = parse_xml(f'<w:rPr {nsdecls("w")}><w:sz w:val="2"/><w:szCs w:val="2"/></w:rPr>')
    insert_in_order(entries._p.get_or_add_pPr(), mark, PPR_ORDER)


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


def is_sub_bullet(line: str) -> bool:
    """A "- " bullet indented by two or more spaces."""
    return line.startswith(("  ", "	")) and line.strip().startswith("- ")


def next_line_is_sub_bullet(lines: list[str], index: int) -> bool:
    for line in lines[index + 1:]:
        if line.strip():
            return is_sub_bullet(line)
    return False


LEAD_IN_LIMIT = 4
STYLE_PREFIX = re.compile(r"^\{([^}]+)\}\s+(.*)$")


def leads_to_table_code_or_image(lines: list[str], index: int) -> bool:
    """True when only plain paragraphs, at most LEAD_IN_LIMIT of them counting this one, stand
    between this line and a table, code block or image."""
    paragraphs = 1
    for line in lines[index + 1:]:
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.startswith(("|", "```", "![")):
            return True
        if stripped.startswith(("#", "- ", "**")) or re.match(r"^\d+\. ", stripped):
            return False
        paragraphs += 1
        if paragraphs > LEAD_IN_LIMIT:
            return False
    return False


def build(name: str, output: Path) -> None:
    lines = (SOURCE / f"{name}.md").read_text(encoding="utf-8-sig").splitlines()
    meta, lines = parse_front_matter(lines)
    body_style = meta.get("body", "Normal")
    doc = Document()
    setup(doc)
    style_names = {s.name for s in doc.styles}
    if body_style not in style_names:
        raise SystemExit(f"{name}: unknown body style {body_style}")
    lists = Lists(doc)
    cover(doc, meta)
    if meta.get("toc") == "true":
        contents(doc, meta, body_style)

    code_block_ends = []
    lead_in = False
    bullet_list = None
    numbered_list = None
    i = 0
    while i < len(lines):
        line = lines[i].rstrip()
        stripped = line.strip()
        if not stripped:
            i += 1
            continue
        numbered = re.match(r"^\d+\. ", stripped)
        if not numbered:
            numbered_list = None
        if not stripped.startswith("- "):
            bullet_list = None
        if stripped.startswith("```"):
            lead_in = False
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
            lead_in = False
            rows = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append([v.strip() for v in lines[i].strip().strip("|").split("|")])
                i += 1
            table(doc, rows)
            continue
        image_match = re.fullmatch(r"!\[([^]]*)\]\(([^)]+)\)", stripped)
        if image_match:
            lead_in = False
            figure(doc, image_match.group(1), SOURCE / image_match.group(2))
            i += 1
            continue
        if stripped.startswith("### "):
            heading = doc.add_paragraph(style="Heading 2")
            inline(heading, stripped[4:])
            use_text_fonts(heading)
            lead_in = True
        elif stripped.startswith("## "):
            heading = doc.add_paragraph(style="Heading 1")
            inline(heading, stripped[3:])
            use_text_fonts(heading)
            lead_in = True
        elif re.fullmatch(r"\*\*[^*]+\*\*", stripped):
            heading = doc.add_paragraph(style="Heading 3")
            inline(heading, stripped[2:-2])
            use_text_fonts(heading)
            lead_in = True
        elif stripped.startswith("- "):
            lead_in = False
            if bullet_list is None:
                bullet_list = lists.new(numbered=False)
            bullet = list_item(doc, bullet_list, 1 if is_sub_bullet(line) else 0)
            # A bullet that only names the group of bullets under it stays with the first of them.
            if not is_sub_bullet(line) and next_line_is_sub_bullet(lines, i):
                bullet.paragraph_format.keep_with_next = True
            inline(bullet, stripped[2:])
        elif numbered:
            lead_in = False
            if numbered_list is None:
                numbered_list = lists.new(numbered=True)
            inline(list_item(doc, numbered_list, 0), stripped.partition(" ")[2])
        else:
            style_match = STYLE_PREFIX.match(stripped)
            style = body_style
            text = stripped
            if style_match:
                style, text = style_match.groups()
                if style not in style_names:
                    raise SystemExit(f"{name}: unknown paragraph style {style}")
            paragraph = doc.add_paragraph(style=style)
            # A sentence that introduces a table, list or code block stays with it. So do the few
            # paragraphs between a heading and the first table, code block or image of its section,
            # so the heading and those paragraphs are not left at the foot of a page.
            paragraph.paragraph_format.keep_with_next = text.endswith("：") or (
                lead_in and leads_to_table_code_or_image(lines, i))
            inline(paragraph, text)
        i += 1

    separate_code_blocks(doc, code_block_ends)
    separate_tables(doc)
    plain = meta.get("cover") == "plain"
    now = datetime.now(timezone.utc)
    props = doc.core_properties
    # Word puts the name of its user into "last modified by" when Update-WordFields.ps1 saves
    # the file; that script sets it back to JET.
    props.author = "JET"
    props.last_modified_by = "JET"
    props.title = meta["title"]
    props.subject = "" if plain else f"{PRODUCT} {VERSION}"
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
