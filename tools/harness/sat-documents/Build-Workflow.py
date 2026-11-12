# 產生 docs/SAT/Workflow.xlsx：一張泳道流程圖，描述從開發、測試、Staging、SAT 評估到正式部署的流程。
#
# 來源是 source/workflow.json，記錄泳道、階段帶、每個圖形與每條連接線。這支腳本直接寫出 xlsx 的 XML：
# 表頭與階段帶寫在儲存格並加框線，流程圖用 Excel 的原生流程圖圖形與肘形接點畫出，在 Excel 裡可以選取與編輯。
# 版面設定為橫向 A4、縮成一頁。成品不含任何範本檔的零件，文件屬性的作者固定寫 JET。
#
# 用法（python 指裝有 lxml 的 Python 3）：
#   python tools/harness/sat-documents/Build-Workflow.py
#   python tools/harness/sat-documents/Build-Workflow.py --source <workflow.json> --output <Workflow.xlsx>
#
# 寫檔前的檢查，任何一項不成立就停止，不產生檔案：
#   - 圖形、連接線的識別名稱不重複；泳道與階段帶對得上。
#   - 每個圖形都在它標示的泳道與階段帶裡，圖形與文字標籤互不重疊。
#   - 每條連接線的起點與終點圖形都存在，相鄰兩點同欄或同列，從接點朝外出發、朝內抵達，不穿過其他圖形與文字標籤。
#   - 每個 Pass 或 Fail 標籤指到的連接線都存在，而且那條線標的是同一個結果。
#
# 產生後要用 Excel 開啟並轉成 PDF 看過一次，確認沒有修復提示、箭頭方向正確。
import argparse
import bisect
import io
import json
import math
import re
import sys
import zipfile
from pathlib import Path

from lxml import etree

HERE = Path(__file__).resolve().parent
DEFAULT_SOURCE = HERE / 'source' / 'workflow.json'
DEFAULT_OUTPUT = HERE.parents[2] / 'docs' / 'SAT' / 'Workflow.xlsx'

FONT = '微軟正黑體'
CELL_FONT_SIZE = 12       # 表頭與階段帶；也是活頁簿的預設字型，欄寬以它的數字寬度換算
LABEL_FONT_SIZE = 11      # Pass 與 Fail
SHAPE_FONT_SIZE = 11
DIGIT_WIDTH_PIXELS = 9    # 微軟正黑體 12 點在 96 DPI 的數字寬度
SHAPE_FILL = 'C1E5F5'
SHAPE_OUTLINE = '0F4761'
CONNECTOR_COLOR = '156082'
SHAPE_OUTLINE_WIDTH = 19050   # 1.5 點
CONNECTOR_WIDTH = 28575       # 2.25 點
EMU_PER_PIXEL = 9525
EMU_PER_POINT = 12700
SNAP = 20                     # 折點與接點相差在這個範圍內（EMU）視為對齊

SHAPE_TYPES = {'flowChartTerminator', 'flowChartProcess', 'flowChartDecision', 'flowChartDocument'}
SIDES = {'top': (0, (0, -1)), 'left': (1, (-1, 0)), 'bottom': (2, (0, 1)), 'right': (3, (1, 0))}

NS = {
    'main': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main',
    'r': 'http://schemas.openxmlformats.org/officeDocument/2006/relationships',
    'xdr': 'http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing',
    'a': 'http://schemas.openxmlformats.org/drawingml/2006/main',
    'rel': 'http://schemas.openxmlformats.org/package/2006/relationships',
    'ct': 'http://schemas.openxmlformats.org/package/2006/content-types',
    'cp': 'http://schemas.openxmlformats.org/package/2006/metadata/core-properties',
    'dc': 'http://purl.org/dc/elements/1.1/',
}


def stop(message):
    raise SystemExit('Workflow 來源有誤：' + message)


def el(tag, attrib=None, *children, text=None, nsmap=None):
    """建立 XML 節點。tag 寫成「前綴:名稱」，前綴對照 NS。"""
    prefix, name = tag.split(':')
    node = etree.Element('{%s}%s' % (NS[prefix], name), nsmap=nsmap)
    for key, value in (attrib or {}).items():
        if ':' in key:
            key_prefix, key_name = key.split(':')
            key = '{%s}%s' % (NS[key_prefix], key_name)
        node.set(key, str(value))
    for child in children:
        if child is not None:
            node.append(child)
    if text is not None:
        node.text = text
    return node


def column_index(name):
    value = 0
    for ch in name:
        value = value * 26 + ord(ch) - 64
    return value - 1


def column_name(index):
    name = ''
    index += 1
    while index:
        index, rest = divmod(index - 1, 26)
        name = chr(65 + rest) + name
    return name


def parse_cell(ref):
    match = re.fullmatch(r'([A-Z]+)([1-9][0-9]*)', ref)
    if not match:
        stop('儲存格位址 %r 的格式不對。' % ref)
    return column_index(match[1]), int(match[2]) - 1


def parse_range(ref):
    """回傳 (第一欄, 第一列, 最後一欄, 最後一列)，都是從 0 起算的序號。"""
    first, _, last = ref.partition(':')
    c1, r1 = parse_cell(first)
    c2, r2 = parse_cell(last or first)
    if c2 < c1 or r2 < r1:
        stop('範圍 %r 的起點在終點之後。' % ref)
    return c1, r1, c2, r2


class Grid:
    """儲存格座標與 EMU 之間的換算。"""

    def __init__(self, sheet):
        self.columns = sheet['columnCount']
        self.rows = sheet['rowCount']
        pixels = [sheet['columnWidthPixels']] * self.columns
        for name, value in sheet.get('columnWidthPixelsByColumn', {}).items():
            pixels[column_index(name)] = value
        self.column_pixels = pixels
        self.column_emu = [value * EMU_PER_PIXEL for value in pixels]
        self.column_edges = [0]
        for value in self.column_emu:
            self.column_edges.append(self.column_edges[-1] + value)
        self.row_points = sheet['rowHeightPoints']
        self.row_emu = round(self.row_points * EMU_PER_POINT)

    def x(self, units):
        index = math.floor(units)
        if not 0 <= index < self.columns:
            stop('橫向座標 %s 超出工作表的欄數。' % units)
        return self.column_edges[index] + round((units - index) * self.column_emu[index])

    def y(self, units):
        if not 0 <= units <= self.rows:
            stop('縱向座標 %s 超出工作表的列數。' % units)
        return round(units * self.row_emu)

    def marker(self, tag, x, y):
        column = min(bisect.bisect_right(self.column_edges, x) - 1, self.columns - 1)
        row, row_offset = divmod(y, self.row_emu)
        return el(tag, None,
                  el('xdr:col', text=str(column)), el('xdr:colOff', text=str(x - self.column_edges[column])),
                  el('xdr:row', text=str(row)), el('xdr:rowOff', text=str(row_offset)))

    def column_width(self, index):
        """Excel 的欄寬單位是預設字型的數字寬度，存檔時取到 1/256。"""
        return math.floor(self.column_pixels[index] / DIGIT_WIDTH_PIXELS * 256) / 256


class Box:
    def __init__(self, name, left, top, right, bottom, kind='rect'):
        self.name, self.left, self.top, self.right, self.bottom, self.kind = name, left, top, right, bottom, kind

    @property
    def center(self):
        return (self.left + self.right) // 2, (self.top + self.bottom) // 2

    def overlaps(self, other):
        """兩個外框是否重疊。其中一個是菱形時，改用菱形的實際範圍判斷。"""
        if self.left >= other.right or other.left >= self.right or self.top >= other.bottom or other.top >= self.bottom:
            return False
        if self.kind == 'diamond' and other.kind == 'diamond':
            return True
        if self.kind == 'diamond':
            return self._diamond_touches(other)
        if other.kind == 'diamond':
            return other._diamond_touches(self)
        return True

    def _diamond_touches(self, rect):
        cx, cy = self.center
        nearest_x = min(max(cx, rect.left), rect.right)
        nearest_y = min(max(cy, rect.top), rect.bottom)
        half_w, half_h = (self.right - self.left) / 2, (self.bottom - self.top) / 2
        return abs(nearest_x - cx) / half_w + abs(nearest_y - cy) / half_h < 1

    def crossed_by(self, a, b):
        """水平或垂直線段 a 到 b 是否穿過外框內部。"""
        margin = CONNECTOR_WIDTH // 2
        left, right, top, bottom = self.left + margin, self.right - margin, self.top + margin, self.bottom - margin
        if a[1] == b[1]:
            return top < a[1] < bottom and max(min(a[0], b[0]), left) < min(max(a[0], b[0]), right)
        return left < a[0] < right and max(min(a[1], b[1]), top) < min(max(a[1], b[1]), bottom)


def require_no_overlap(boxes, others):
    """boxes 裡的外框不可互相重疊，也不可和 others 的外框重疊。"""
    for index, first in enumerate(boxes):
        for second in boxes[index + 1:] + others:
            if first.overlaps(second):
                stop('%r 和 %r 重疊。' % (first.name, second.name))


def connection_site(shape, side):
    box = shape['box']
    cx, cy = box.center
    if side == 'top':
        return cx, box.top
    if side == 'left':
        return box.left, cy
    if side == 'right':
        return box.right, cy
    if shape['type'] == 'flowChartDocument':
        # 文件圖形的下緣是波浪線，下方接點在高度的 20172/21600 處。
        return cx, box.top + round((box.bottom - box.top) * 20172 / 21600)
    return cx, box.bottom


def sign(value):
    return (value > 0) - (value < 0)


def connector_geometry(points):
    """把折線換成 Excel 的接點圖形：種類、旋轉、翻轉、未旋轉的寬高與調整值。

    預設接點在自己的座標裡從左上角出發，先水平再垂直，交替到右下角。第一段是垂直線的折線要旋轉 90 或 270 度。
    """
    (x0, y0), (xn, yn) = points[0], points[-1]
    segments = len(points) - 1
    if segments == 1:
        return {'prst': 'straightConnector1', 'rot': 0, 'flipH': xn < x0, 'flipV': yn < y0,
                'cx': abs(xn - x0), 'cy': abs(yn - y0), 'adj': []}
    if segments > 5:
        stop('一條連接線最多五段。')
    horizontal_first = points[0][1] == points[1][1]
    if horizontal_first:
        along = sign(xn - x0) or sign(points[1][0] - x0)
        across = sign(yn - y0) or sign(points[2][1] - y0)
        cx, cy = abs(xn - x0), abs(yn - y0)
        rot, flip_h, flip_v = 0, along < 0, across < 0
        local = lambda p: ((p[0] - x0) * along, (p[1] - y0) * across)
    else:
        along = sign(yn - y0) or sign(points[1][1] - y0)
        across = sign(xn - x0) or sign(points[2][0] - x0)
        cx, cy = abs(yn - y0), abs(xn - x0)
        rot, flip_h, flip_v = {
            (1, -1): (5400000, False, False),
            (1, 1): (16200000, True, False),
            (-1, 1): (16200000, False, False),
            (-1, -1): (16200000, False, True),
        }[(along, across)]
        local = lambda p: ((p[1] - y0) * along, (p[0] - x0) * across)
    adj = []
    for number in range(1, segments - 1):
        u, v = local(points[number])
        value, size = (u, cx) if number % 2 else (v, cy)
        if size == 0:
            stop('連接線的起點與終點在同一條線上，無法用肘形接點表示，請調整折點。')
        adj.append(round(value * 100000 / size))
    return {'prst': 'bentConnector%d' % segments, 'rot': rot, 'flipH': flip_h, 'flipV': flip_v,
            'cx': cx, 'cy': cy, 'adj': adj}


def rendered_path(geometry, center):
    """照 Excel 的規則把接點圖形畫回工作表座標，用來核對 connector_geometry 的結果。"""
    cx, cy, adj = geometry['cx'], geometry['cy'], geometry['adj']
    pos = [value * size / 100000 for value, size in zip(adj, (cx, cy, cx))]
    local = {
        'straightConnector1': [(0, 0), (cx, cy)],
        'bentConnector2': [(0, 0), (cx, 0), (cx, cy)],
        'bentConnector3': lambda: [(0, 0), (pos[0], 0), (pos[0], cy), (cx, cy)],
        'bentConnector4': lambda: [(0, 0), (pos[0], 0), (pos[0], pos[1]), (cx, pos[1]), (cx, cy)],
        'bentConnector5': lambda: [(0, 0), (pos[0], 0), (pos[0], pos[1]), (pos[2], pos[1]), (pos[2], cy), (cx, cy)],
    }[geometry['prst']]
    if callable(local):
        local = local()
    result = []
    for lx, ly in local:
        u, v = lx - cx / 2, ly - cy / 2
        if geometry['flipH']:
            u = -u
        if geometry['flipV']:
            v = -v
        if geometry['rot'] == 5400000:
            u, v = -v, u
        elif geometry['rot'] == 16200000:
            u, v = v, -u
        result.append((center[0] + u, center[1] + v))
    return result


def load(source):
    data = json.loads(source.read_text(encoding='utf-8'))
    grid = Grid(data['sheet'])

    lanes, header_rows = {}, None
    for lane in data['lanes']:
        c1, r1, c2, r2 = parse_range(lane['header'])
        if header_rows not in (None, (r1, r2)):
            stop('泳道表頭必須在同樣的列。')
        header_rows = (r1, r2)
        if lane['id'] in lanes:
            stop('泳道 %r 重複。' % lane['id'])
        lanes[lane['id']] = dict(lane, columns=(c1, c2))
    phases, band_columns = {}, None
    for phase in data['phases']:
        c1, r1, c2, r2 = parse_range(phase['band'])
        if band_columns not in (None, (c1, c2)):
            stop('階段帶必須在同樣的欄。')
        band_columns = (c1, c2)
        if phase['id'] in phases:
            stop('階段帶 %r 重複。' % phase['id'])
        phases[phase['id']] = dict(phase, rows=(r1, r2))

    shapes = {}
    for shape in data['shapes']:
        if shape['id'] in shapes:
            stop('圖形 %r 重複。' % shape['id'])
        if shape['type'] not in SHAPE_TYPES:
            stop('圖形 %r 的種類 %r 不在支援的流程圖圖形裡。' % (shape['id'], shape['type']))
        if shape['lane'] not in lanes or shape['phase'] not in phases:
            stop('圖形 %r 標示的泳道或階段帶不存在。' % shape['id'])
        box = Box(shape['id'], grid.x(shape['left']), grid.y(shape['top']),
                  grid.x(shape['left'] + shape['width']), grid.y(shape['top'] + shape['height']),
                  'diamond' if shape['type'] == 'flowChartDecision' else 'rect')
        c1, c2 = lanes[shape['lane']]['columns']
        r1, r2 = phases[shape['phase']]['rows']
        if not (grid.column_edges[c1] <= box.left and box.right <= grid.column_edges[c2 + 1]
                and r1 * grid.row_emu <= box.top and box.bottom <= (r2 + 1) * grid.row_emu):
            stop('圖形 %r 超出它標示的泳道或階段帶。' % shape['id'])
        shapes[shape['id']] = dict(shape, box=box)
    require_no_overlap([shape['box'] for shape in shapes.values()], [])

    connectors = {}
    for connector in data['connectors']:
        if connector['id'] in connectors:
            stop('連接線 %r 重複。' % connector['id'])
        ends = []
        for key in ('from', 'to'):
            end = connector[key]
            if end['shape'] not in shapes:
                stop('連接線 %r 的%s圖形 %r 不存在。' % (connector['id'], '起點' if key == 'from' else '終點', end['shape']))
            if end['side'] not in SIDES:
                stop('連接線 %r 的接點 %r 不是 top、left、bottom、right 之一。' % (connector['id'], end['side']))
            ends.append((shapes[end['shape']], end['side']))
        points = [list(connection_site(*ends[0]))]
        points += [[grid.x(x), grid.y(y)] for x, y in connector.get('bends', [])]
        points.append(list(connection_site(*ends[1])))
        # 折點與前一點、最後一個折點與終點只差幾個 EMU 時，視為同欄或同列並對齊。
        for index in range(1, len(points) - 1):
            for axis in (0, 1):
                if abs(points[index][axis] - points[index - 1][axis]) <= SNAP:
                    points[index][axis] = points[index - 1][axis]
        if len(points) > 2:
            for axis in (0, 1):
                if abs(points[-2][axis] - points[-1][axis]) <= SNAP:
                    points[-2][axis] = points[-1][axis]
        else:
            for axis in (0, 1):
                if abs(points[1][axis] - points[0][axis]) <= SNAP:
                    points[1][axis] = points[0][axis]
        points = [tuple(p) for p in points]
        previous = None
        for a, b in zip(points, points[1:]):
            if (a[0] == b[0]) == (a[1] == b[1]):
                stop('連接線 %r 有一段不是水平或垂直線，或長度為零。' % connector['id'])
            orientation = 'h' if a[1] == b[1] else 'v'
            if orientation == previous:
                stop('連接線 %r 有連續兩段同方向，請刪掉多餘的折點。' % connector['id'])
            previous = orientation
        out_direction = (sign(points[1][0] - points[0][0]), sign(points[1][1] - points[0][1]))
        in_direction = (sign(points[-1][0] - points[-2][0]), sign(points[-1][1] - points[-2][1]))
        if out_direction != SIDES[ends[0][1]][1]:
            stop('連接線 %r 沒有從起點的 %s 接點朝外出發。' % (connector['id'], ends[0][1]))
        if in_direction != tuple(-value for value in SIDES[ends[1][1]][1]):
            stop('連接線 %r 沒有朝終點的 %s 接點抵達。' % (connector['id'], ends[1][1]))
        geometry = connector_geometry(points)
        center = ((points[0][0] + points[-1][0]) / 2, (points[0][1] + points[-1][1]) / 2)
        tolerance = max(geometry['cx'], geometry['cy']) / 100000 + 2
        drawn = rendered_path(geometry, center)
        if len(drawn) != len(points) or any(abs(p[0] - q[0]) > tolerance or abs(p[1] - q[1]) > tolerance
                                           for p, q in zip(drawn, points)):
            stop('連接線 %r 換算成 Excel 接點後和來源的折線不一致。' % connector['id'])
        connectors[connector['id']] = dict(connector, points=points, geometry=geometry, ends=ends)

    cell_labels, text_boxes = [], []
    for label in data['labels']:
        for name in label['connectors']:
            if name not in connectors:
                stop('標籤 %r 指到的連接線 %r 不存在。' % (label['text'], name))
            if connectors[name].get('label') != label['text']:
                stop('標籤 %r 和連接線 %r 標的結果不一致。' % (label['text'], name))
        if 'cell' in label:
            column, row = parse_cell(label['cell'])
            box = Box('儲存格 %s 的 %s' % (label['cell'], label['text']), grid.column_edges[column], row * grid.row_emu,
                      grid.column_edges[column + 1], (row + 1) * grid.row_emu)
            cell_labels.append(dict(label, column=column, row=row, box=box))
        else:
            box = Box('文字方塊 %s' % label['text'], grid.x(label['left']), grid.y(label['top']),
                      grid.x(label['left'] + label['width']), grid.y(label['top'] + label['height']))
            text_boxes.append(dict(label, box=box))
    labelled = {name for label in data['labels'] for name in label['connectors']}
    for connector in connectors.values():
        if connector.get('label') and connector['id'] not in labelled:
            stop('連接線 %r 標了 %s，但 labels 沒有對應的文字。' % (connector['id'], connector['label']))

    label_boxes = [label['box'] for label in cell_labels + text_boxes]
    require_no_overlap(label_boxes, [shape['box'] for shape in shapes.values()])
    boxes = [shape['box'] for shape in shapes.values()] + label_boxes
    for connector in connectors.values():
        points = connector['points']
        for index, (a, b) in enumerate(zip(points, points[1:])):
            for box in boxes:
                own_start = index == 0 and box is connector['ends'][0][0]['box']
                own_end = index == len(points) - 2 and box is connector['ends'][1][0]['box']
                if not own_start and not own_end and box.crossed_by(a, b):
                    stop('連接線 %r 穿過 %r。' % (connector['id'], box.name))

    return {'data': data, 'grid': grid, 'lanes': lanes, 'phases': phases, 'header_rows': header_rows,
            'band_columns': band_columns, 'shapes': shapes, 'connectors': connectors,
            'cell_labels': cell_labels, 'text_boxes': text_boxes}


def build_cells(model):
    """算出每個儲存格的文字、對齊與框線，以及合併範圍。"""
    cells, merges = {}, []

    def cell(row, column):
        return cells.setdefault((row, column), {'text': None, 'align': None, 'font': 0, 'borders': set()})

    def outline(c1, r1, c2, r2):
        for column in range(c1, c2 + 1):
            cell(r1, column)['borders'].add('top')
            cell(r2, column)['borders'].add('bottom')
        for row in range(r1, r2 + 1):
            cell(row, c1)['borders'].add('left')
            cell(row, c2)['borders'].add('right')

    def write(ref, text, font=0):
        c1, r1, c2, r2 = parse_range(ref)
        target = cell(r1, c1)
        if target['text'] is not None:
            stop('儲存格 %s 被寫了兩次。' % ref)
        target.update(text=text, font=font)
        for row in range(r1, r2 + 1):
            for column in range(c1, c2 + 1):
                cell(row, column)['align'] = 'center'
        if (c1, r1) != (c2, r2):
            merges.append(ref)

    for lane in model['lanes'].values():
        outline(*parse_range(lane['header']))
        write(lane['header'], lane['text'])
    for phase in model['phases'].values():
        outline(*parse_range(phase['band']))
        write(phase['band'], phase['text'])
        for lane in model['lanes'].values():
            outline(lane['columns'][0], phase['rows'][0], lane['columns'][1], phase['rows'][1])
    for entry in model['data'].get('cells', []):
        write(entry['range'], entry['text'])
    for label in model['cell_labels']:
        write(label['cell'], label['text'], font=1)
    return cells, merges


def styles_xml(cells):
    borders, xfs = [()], [((), None, 0)]
    for value in cells.values():
        key = (tuple(side for side in ('left', 'right', 'top', 'bottom') if side in value['borders']),
               value['align'], value['font'])
        if key[0] not in borders:
            borders.append(key[0])
        if key not in xfs:
            xfs.append(key)
        value['style'] = xfs.index(key)

    def font(size):
        return el('main:font', None, el('main:sz', {'val': size}), el('main:color', {'rgb': 'FF000000'}),
                  el('main:name', {'val': FONT}), el('main:family', {'val': 2}), el('main:charset', {'val': 136}))

    def border(sides):
        def side(name):
            if name not in sides:
                return el('main:' + name)
            return el('main:' + name, {'style': 'thin'}, el('main:color', {'rgb': 'FF000000'}))
        return el('main:border', None, side('left'), side('right'), side('top'), side('bottom'), el('main:diagonal'))

    def xf(key):
        sides, align, font_id = key
        alignment = {'vertical': 'center'}
        if align == 'center':
            alignment.update(horizontal='center', wrapText=1)
        return el('main:xf', {'numFmtId': 0, 'fontId': font_id, 'fillId': 0, 'borderId': borders.index(sides), 'xfId': 0,
                              'applyFont': 1, 'applyBorder': 1, 'applyAlignment': 1},
                  el('main:alignment', alignment))

    return el('main:styleSheet', None,
              el('main:fonts', {'count': 2}, font(CELL_FONT_SIZE), font(LABEL_FONT_SIZE)),
              el('main:fills', {'count': 2},
                 el('main:fill', None, el('main:patternFill', {'patternType': 'none'})),
                 el('main:fill', None, el('main:patternFill', {'patternType': 'gray125'}))),
              el('main:borders', {'count': len(borders)}, *[border(sides) for sides in borders]),
              el('main:cellStyleXfs', {'count': 1},
                 el('main:xf', {'numFmtId': 0, 'fontId': 0, 'fillId': 0, 'borderId': 0})),
              el('main:cellXfs', {'count': len(xfs)}, *[xf(key) for key in xfs]),
              el('main:cellStyles', {'count': 1}, el('main:cellStyle', {'name': 'Normal', 'xfId': 0, 'builtinId': 0})),
              nsmap={None: NS['main']})


def sheet_xml(model, cells, merges):
    grid, sheet = model['grid'], model['data']['sheet']
    cols = el('main:cols')
    for index in range(grid.columns):
        cols.append(el('main:col', {'min': index + 1, 'max': index + 1, 'width': repr(grid.column_width(index)),
                                    'customWidth': 1}))
    sheet_data = el('main:sheetData')
    for row in range(grid.rows):
        node = el('main:row', {'r': row + 1, 'ht': repr(grid.row_points), 'customHeight': 1})
        for column in range(grid.columns):
            value = cells.get((row, column))
            if value is None:
                continue
            ref = column_name(column) + str(row + 1)
            if value['text'] is None:
                node.append(el('main:c', {'r': ref, 's': value['style']}))
            else:
                text = el('main:t', text=value['text'])
                text.set('{http://www.w3.org/XML/1998/namespace}space', 'preserve')
                node.append(el('main:c', {'r': ref, 's': value['style'], 't': 'inlineStr'}, el('main:is', None, text)))
        sheet_data.append(node)
    return el('main:worksheet', None,
              el('main:sheetPr', None, el('main:pageSetUpPr', {'fitToPage': 1})),
              el('main:dimension', {'ref': sheet['printArea']}),
              el('main:sheetViews', None,
                 el('main:sheetView', {'showGridLines': 0, 'tabSelected': 1, 'zoomScale': sheet['zoomPercent'],
                                       'zoomScaleNormal': sheet['zoomPercent'], 'workbookViewId': 0})),
              el('main:sheetFormatPr', {'defaultColWidth': repr(grid.column_width(0)),
                                        'defaultRowHeight': repr(grid.row_points), 'customHeight': 1}),
              cols, sheet_data,
              el('main:mergeCells', {'count': len(merges)}, *[el('main:mergeCell', {'ref': ref}) for ref in merges]),
              el('main:printOptions', {'horizontalCentered': 1, 'verticalCentered': 1}),
              el('main:pageMargins', {'left': '0.4', 'right': '0.4', 'top': '0.4', 'bottom': '0.4',
                                      'header': '0.2', 'footer': '0.2'}),
              el('main:pageSetup', {'paperSize': 9, 'orientation': 'landscape', 'fitToWidth': 1, 'fitToHeight': 1}),
              el('main:drawing', {'r:id': 'rId1'}),
              nsmap={None: NS['main'], 'r': NS['r']})


def text_body(text, body_attributes, autofit, centered):
    paragraphs = []
    for line in text.split('\n'):
        run = el('a:r', None,
                 el('a:rPr', {'lang': 'zh-TW', 'altLang': 'en-US', 'sz': SHAPE_FONT_SIZE * 100},
                    el('a:solidFill', None, el('a:srgbClr', {'val': '000000'})),
                    el('a:latin', {'typeface': FONT}), el('a:ea', {'typeface': FONT})),
                 el('a:t', text=line))
        paragraphs.append(el('a:p', None, el('a:pPr', {'algn': 'ctr'}) if centered else None, run))
    return el('xdr:txBody', None, el('a:bodyPr', body_attributes, el(autofit)), el('a:lstStyle'), *paragraphs)


def drawing_xml(model):
    grid = model['grid']
    root = el('xdr:wsDr', nsmap={'xdr': NS['xdr'], 'a': NS['a']})
    next_id = 2
    shape_ids = {}

    for shape in model['shapes'].values():
        box = shape['box']
        shape_ids[shape['id']] = next_id
        root.append(el('xdr:twoCellAnchor', None,
                       grid.marker('xdr:from', box.left, box.top), grid.marker('xdr:to', box.right, box.bottom),
                       el('xdr:sp', {'macro': '', 'textlink': ''},
                          el('xdr:nvSpPr', None,
                             el('xdr:cNvPr', {'id': next_id, 'name': shape['text'].replace('\n', ' ')}),
                             el('xdr:cNvSpPr')),
                          el('xdr:spPr', None,
                             el('a:xfrm', None, el('a:off', {'x': box.left, 'y': box.top}),
                                el('a:ext', {'cx': box.right - box.left, 'cy': box.bottom - box.top})),
                             el('a:prstGeom', {'prst': shape['type']}, el('a:avLst')),
                             el('a:solidFill', None, el('a:srgbClr', {'val': SHAPE_FILL})),
                             el('a:ln', {'w': SHAPE_OUTLINE_WIDTH},
                                el('a:solidFill', None, el('a:srgbClr', {'val': SHAPE_OUTLINE})))),
                          text_body(shape['text'],
                                    {'vertOverflow': 'clip', 'horzOverflow': 'overflow', 'wrap': 'none', 'lIns': 0,
                                     'tIns': 0, 'rIns': 0, 'bIns': 0, 'rtlCol': 0, 'anchor': 'ctr'},
                                    'a:noAutofit', centered=True)),
                       el('xdr:clientData')))
        next_id += 1

    for connector in model['connectors'].values():
        points, geometry = connector['points'], connector['geometry']
        (x0, y0), (xn, yn) = points[0], points[-1]
        # 旋轉 90 或 270 度時，xfrm 記未旋轉的外框，中心與畫面上的外框相同；儲存格錨點記畫面上的外框。
        off_x = round((x0 + xn) / 2 - geometry['cx'] / 2)
        off_y = round((y0 + yn) / 2 - geometry['cy'] / 2)
        xfrm = {}
        if geometry['rot']:
            xfrm['rot'] = geometry['rot']
        if geometry['flipH']:
            xfrm['flipH'] = 1
        if geometry['flipV']:
            xfrm['flipV'] = 1
        guides = [el('a:gd', {'name': 'adj%d' % (index + 1), 'fmla': 'val %d' % value})
                  for index, value in enumerate(geometry['adj'])]
        (start_shape, start_side), (end_shape, end_side) = connector['ends']
        name = '%s 到 %s' % (start_shape['text'], end_shape['text'])
        if connector.get('label'):
            name += '（%s）' % connector['label']
        root.append(el('xdr:twoCellAnchor', None,
                       grid.marker('xdr:from', min(x0, xn), min(y0, yn)),
                       grid.marker('xdr:to', max(x0, xn), max(y0, yn)),
                       el('xdr:cxnSp', {'macro': ''},
                          el('xdr:nvCxnSpPr', None,
                             el('xdr:cNvPr', {'id': next_id, 'name': name.replace('\n', ' ')}),
                             el('xdr:cNvCxnSpPr', None,
                                el('a:stCxn', {'id': shape_ids[start_shape['id']], 'idx': SIDES[start_side][0]}),
                                el('a:endCxn', {'id': shape_ids[end_shape['id']], 'idx': SIDES[end_side][0]}))),
                          el('xdr:spPr', None,
                             el('a:xfrm', xfrm, el('a:off', {'x': off_x, 'y': off_y}),
                                el('a:ext', {'cx': geometry['cx'], 'cy': geometry['cy']})),
                             el('a:prstGeom', {'prst': geometry['prst']}, el('a:avLst', None, *guides)),
                             el('a:ln', {'w': CONNECTOR_WIDTH, 'cap': 'flat'},
                                el('a:solidFill', None, el('a:srgbClr', {'val': CONNECTOR_COLOR})),
                                el('a:prstDash', {'val': 'solid'}),
                                el('a:miter', {'lim': 800000}),
                                el('a:headEnd', {'type': 'none'}),
                                el('a:tailEnd', {'type': 'triangle', 'w': 'lg', 'len': 'lg'})))),
                       el('xdr:clientData')))
        next_id += 1

    for label in model['text_boxes']:
        box = label['box']
        root.append(el('xdr:oneCellAnchor', None,
                       grid.marker('xdr:from', box.left, box.top),
                       el('xdr:ext', {'cx': box.right - box.left, 'cy': box.bottom - box.top}),
                       el('xdr:sp', {'macro': '', 'textlink': ''},
                          el('xdr:nvSpPr', None,
                             el('xdr:cNvPr', {'id': next_id, 'name': '標籤 ' + label['text']}),
                             el('xdr:cNvSpPr', {'txBox': 1})),
                          el('xdr:spPr', None,
                             el('a:xfrm', None, el('a:off', {'x': box.left, 'y': box.top}),
                                el('a:ext', {'cx': box.right - box.left, 'cy': box.bottom - box.top})),
                             el('a:prstGeom', {'prst': 'rect'}, el('a:avLst')),
                             el('a:noFill'),
                             el('a:ln', None, el('a:noFill'))),
                          text_body(label['text'],
                                    {'vertOverflow': 'clip', 'horzOverflow': 'clip', 'wrap': 'none', 'rtlCol': 0,
                                     'anchor': 't'},
                                    'a:spAutoFit', centered=False)),
                       el('xdr:clientData')))
        next_id += 1
    return root


def package_parts(model):
    sheet = model['data']['sheet']
    cells, merges = build_cells(model)
    styles = styles_xml(cells)
    c1, r1, c2, r2 = parse_range(sheet['printArea'])
    print_area = "'%s'!$%s$%d:$%s$%d" % (sheet['name'].replace("'", "''"), column_name(c1), r1 + 1, column_name(c2), r2 + 1)
    office = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships/'

    def relationships(*items):
        return el('rel:Relationships', None,
                  *[el('rel:Relationship', {'Id': 'rId%d' % (index + 1), 'Type': kind, 'Target': target})
                    for index, (kind, target) in enumerate(items)],
                  nsmap={None: NS['rel']})

    content_types = el('ct:Types', None,
                       el('ct:Default', {'Extension': 'rels', 'ContentType': 'application/vnd.openxmlformats-package.relationships+xml'}),
                       el('ct:Default', {'Extension': 'xml', 'ContentType': 'application/xml'}),
                       *[el('ct:Override', {'PartName': part, 'ContentType': kind}) for part, kind in (
                           ('/xl/workbook.xml', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'),
                           ('/xl/worksheets/sheet1.xml', 'application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'),
                           ('/xl/styles.xml', 'application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml'),
                           ('/xl/drawings/drawing1.xml', 'application/vnd.openxmlformats-officedocument.drawing+xml'),
                           ('/docProps/core.xml', 'application/vnd.openxmlformats-package.core-properties+xml'),
                       )],
                       nsmap={None: NS['ct']})
    workbook = el('main:workbook', None,
                  el('main:bookViews', None, el('main:workbookView', {'activeTab': 0})),
                  el('main:sheets', None, el('main:sheet', {'name': sheet['name'], 'sheetId': 1, 'r:id': 'rId1'})),
                  el('main:definedNames', None,
                     el('main:definedName', {'name': '_xlnm.Print_Area', 'localSheetId': 0}, text=print_area)),
                  nsmap={None: NS['main'], 'r': NS['r']})
    core = el('cp:coreProperties', None, el('dc:title', text=sheet['name']), el('dc:creator', text='JET'),
              nsmap={'cp': NS['cp'], 'dc': NS['dc']})
    return [
        ('[Content_Types].xml', content_types),
        ('_rels/.rels', relationships(
            (office + 'officeDocument', 'xl/workbook.xml'),
            ('http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties', 'docProps/core.xml'))),
        ('docProps/core.xml', core),
        ('xl/workbook.xml', workbook),
        ('xl/_rels/workbook.xml.rels', relationships((office + 'worksheet', 'worksheets/sheet1.xml'),
                                                     (office + 'styles', 'styles.xml'))),
        ('xl/styles.xml', styles),
        ('xl/worksheets/sheet1.xml', sheet_xml(model, cells, merges)),
        ('xl/worksheets/_rels/sheet1.xml.rels', relationships((office + 'drawing', '../drawings/drawing1.xml'))),
        ('xl/drawings/drawing1.xml', drawing_xml(model)),
    ]


def main():
    parser = argparse.ArgumentParser(description='從 workflow.json 產生 Workflow.xlsx。')
    parser.add_argument('--source', type=Path, default=DEFAULT_SOURCE)
    parser.add_argument('--output', type=Path, default=DEFAULT_OUTPUT)
    arguments = parser.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')

    model = load(arguments.source)
    buffer = io.BytesIO()
    # 壓縮檔裡的時間固定，來源沒變時重新產生的檔案內容相同。
    with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED) as package:
        for name, root in package_parts(model):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(info, etree.tostring(root, xml_declaration=True, encoding='UTF-8', standalone=True))

    # 讀回剛寫好的內容，確認圖形與連接線的數量和來源相同。
    with zipfile.ZipFile(io.BytesIO(buffer.getvalue())) as package:
        drawing = etree.fromstring(package.read('xl/drawings/drawing1.xml'))
    written_shapes = len(drawing.findall('.//{%s}sp' % NS['xdr']))
    written_connectors = len(drawing.findall('.//{%s}cxnSp' % NS['xdr']))
    expected_shapes = len(model['shapes']) + len(model['text_boxes'])
    if written_shapes != expected_shapes or written_connectors != len(model['connectors']):
        raise SystemExit('寫出的圖形或連接線數量和來源不一致。')

    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_bytes(buffer.getvalue())
    print('已產生 %s：流程圖圖形 %d 個、文字方塊 %d 個、連接線 %d 條。'
          % (arguments.output.name, len(model['shapes']), len(model['text_boxes']), len(model['connectors'])))


if __name__ == '__main__':
    main()
