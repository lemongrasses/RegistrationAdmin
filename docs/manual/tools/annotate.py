# -*- coding: utf-8 -*-
"""把原始截圖裁切、遮蔽敏感資訊並加上編號標記框，輸出到 docs/manual/images/。

用法：python docs/manual/tools/annotate.py <原始截圖資料夾>
原始截圖不進版控（可能含登入帳號），只提交處理後的圖片。
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

RAW = sys.argv[1] if len(sys.argv) > 1 else "shots"
OUT = os.path.join(os.path.dirname(__file__), "..", "images")
RED = (229, 72, 77)
FONT_NUM = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 22)
FONT_CJK = ImageFont.truetype("C:/Windows/Fonts/msjh.ttc", 18)
OUT_WIDTH = 1200

# 每張圖：(輸出檔名, 原始檔, 裁切框, 遮蔽清單[(框, 替代文字)], 標記清單[框])
# 座標皆為原始截圖座標；標記依清單順序編號 1, 2, 3…
PW = lambda w, h: (8, 0, w - 9, h - 9)          # PrintWindow：去掉左、右、下黑邊
SC = lambda w, h: (9, 0, w - 9, h - 9)          # 螢幕擷取：去掉左右、下方陰影邊

FIGS = [
    ("01-pending.png", "raw-01-pending.png", PW(1600, 1000), [], [
        (27, 145, 262, 197), (27, 203, 262, 310), (27, 860, 262, 905), (35, 925, 200, 975),
        (305, 65, 870, 135), (1415, 60, 1565, 113), (308, 153, 1562, 287), (333, 400, 1038, 457),
        (333, 468, 730, 518), (748, 472, 870, 516), (310, 543, 1540, 585), (310, 590, 1540, 657),
        (330, 920, 650, 955)]),
    ("02-filters.png", "raw-02-all-filters.png", PW(1600, 1000), [], [
        (436, 471, 510, 516), (350, 545, 1475, 625), (352, 640, 472, 672), (490, 640, 572, 672),
        (840, 780, 1140, 900)]),
    ("03-duplicates.png", "raw-22-dups.png", (8, 0, 1591, 760), [], [
        (620, 471, 730, 516), (333, 330, 690, 385), (1350, 600, 1480, 720)]),
    ("04-detail-top.png", "raw-04-detail-top.png", PW(1600, 1300), [], [
        (305, 58, 415, 88), (305, 100, 490, 140), (305, 142, 555, 168), (1285, 115, 1565, 168),
        (310, 205, 1540, 288), (330, 330, 630, 395), (472, 680, 615, 705), (960, 415, 1517, 500),
        (960, 525, 1120, 552), (960, 583, 1517, 668), (960, 750, 1517, 910), (960, 925, 1092, 972),
        (960, 990, 1517, 1088), (330, 1118, 640, 1285)]),
    ("05-detail-issues.png", "raw-05-detail-mid.png", PW(1600, 1300), [], [
        (978, 828, 1070, 858), (978, 922, 1128, 967), (960, 1160, 1517, 1290)]),
    ("06-detail-bottom.png", "raw-06-detail-bottom.png", PW(1600, 1300), [], [
        (960, 1005, 1030, 1037), (960, 1058, 1115, 1090), (960, 1110, 1062, 1142)]),
    ("07-invoice.png", "raw-07a-detail-invoice.png", PW(1600, 1300), [], [
        (960, 430, 1517, 505), (960, 518, 1517, 592), (960, 605, 1517, 680), (960, 690, 1517, 808),
        (960, 825, 1517, 1000)]),
    ("08-override.png", "raw-07-detail-expanders.png", PW(1600, 1300), [], [
        (960, 495, 1517, 570), (960, 760, 1517, 838), (960, 848, 1517, 925), (960, 935, 1517, 1053),
        (960, 1065, 1517, 1146)]),
    ("09-flow-step1.png", "raw-08-flow-step1.png", PW(1600, 1300), [], [
        (412, 105, 487, 135), (1100, 128, 1275, 158), (962, 407, 1515, 520), (960, 578, 1517, 628),
        (958, 950, 1519, 1013), (962, 1045, 1500, 1100)]),
    ("10-flow-blocked.png", "raw-09-flow-blocked.png", (8, 0, 1591, 760), [], [
        (962, 407, 1515, 520), (962, 535, 1515, 648)]),
    ("11-other-actions.png", "raw-10-other-actions.png", (600, 780, 1591, 1150), [], [
        (960, 948, 1094, 1002), (963, 1000, 1227, 1137)]),
    ("12-reject-dialog.png", "raw-11-reject-dialog.png", (509, 454, 1092, 838), [], [
        (539, 686, 1061, 734), (950, 764, 1062, 812)]),
    ("13-accept-issue.png", "raw-12-accept-issue.png", (509, 454, 1092, 838), [], [
        (539, 686, 1061, 734), (930, 764, 1062, 812)]),
    ("14-discard-dialog.png", "raw-13-discard.png", (509, 530, 1092, 764), [], []),
    ("15-leave-dialog.png", "raw-18-leave.png", (509, 530, 1092, 764), [], []),
    ("16-payment.png", "raw-15-pay-section.png", PW(1600, 1300), [], [
        (960, 465, 1517, 580), (960, 805, 1517, 882), (960, 895, 1517, 972), (960, 985, 1240, 1060),
        (960, 1085, 1100, 1113)]),
    ("17-payment-more.png", "raw-16-pay-more.png", PW(1600, 1300), [], [
        (960, 535, 1165, 612), (960, 625, 1517, 700), (960, 713, 1517, 958), (960, 965, 1517, 1042),
        (960, 1055, 1517, 1172)]),
    ("18-payment-confirm.png", "raw-17-pay-confirm.png", PW(1600, 1300), [], [
        (962, 304, 1515, 470), (960, 860, 1500, 992)]),
    ("19-refreshing.png", "raw-19-refresh.png", PW(1600, 1300), [], [
        (1415, 60, 1565, 113), (797, 1205, 1075, 1257)]),
    ("20-notice-success.png", "raw-25b-checked.png", (790, 1190, 1080, 1278), [], []),
    ("21-notice-info.png", "raw-12-accept-issue.png", (760, 1190, 1110, 1278), [], []),
    ("22-export.png", "raw-23b-export-adv.png", (8, 0, 1591, 1150), [], [
        (335, 215, 1233, 556), (333, 585, 645, 690), (333, 718, 580, 822), (333, 850, 440, 880),
        (333, 900, 630, 1012), (333, 1070, 495, 1102), (1093, 1060, 1247, 1110)]),
    ("23-settings.png", "raw-25b-checked.png", PW(1600, 1300), [
        ((552, 300, 800, 330), "admin@example.test"),
        ((355, 915, 1480, 955), "https://docs.google.com/spreadsheets/d/（試算表 ID）/edit"),
        ((355, 1043, 1342, 1083), "（OAuth 用戶端檔路徑）")], [
        (310, 155, 1510, 207), (470, 298, 545, 330), (470, 340, 1080, 372), (333, 388, 451, 440),
        (458, 388, 575, 440), (310, 485, 1510, 637), (308, 656, 504, 706), (333, 875, 1485, 958),
        (333, 1003, 1485, 1087), (333, 1130, 481, 1181)]),
    ("24-settings-advanced.png", "raw-27-health.png", PW(1600, 1300), [
        ((355, 28, 1342, 64), "（OAuth 用戶端檔路徑）"),
        ((412, 1030, 990, 1056), "%LOCALAPPDATA%\\RegistrationAdmin\\logs（保留 14 天）")], [
        (333, 282, 1485, 365), (333, 404, 503, 456), (510, 404, 702, 458), (310, 538, 1510, 697),
        (333, 813, 500, 863), (506, 813, 678, 863), (333, 870, 1485, 1023), (333, 1058, 940, 1082)]),
    ("25-first-run.png", "raw-30-firstrun.png", PW(1600, 1000), [], [
        (333, 288, 1082, 342), (333, 418, 540, 470), (470, 612, 830, 644)]),
]


def badge(draw, x, y, n):
    r = 17
    draw.ellipse((x - r, y - r, x + r, y + r), fill=RED, outline="white", width=3)
    t = str(n)
    w = draw.textlength(t, font=FONT_NUM)
    draw.text((x - w / 2, y - 13), t, font=FONT_NUM, fill="white")


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, src, crop, redactions, marks in FIGS:
        img = Image.open(os.path.join(RAW, src)).convert("RGB")
        d = ImageDraw.Draw(img)
        for (x0, y0, x1, y1), text in redactions:
            d.rectangle((x0, y0, x1, y1), fill=(255, 255, 255))
            d.text((x0 + 4, y0 + (y1 - y0 - 22) / 2), text, font=FONT_CJK, fill=(90, 98, 112))
        for i, (x0, y0, x1, y1) in enumerate(marks, 1):
            d.rounded_rectangle((x0 - 3, y0 - 3, x1 + 3, y1 + 3), radius=8, outline=RED, width=3)
        for i, (x0, y0, x1, y1) in enumerate(marks, 1):
            bx = max(crop[0] + 19, x0 - 6)
            by = max(crop[1] + 19, y0 - 6)
            badge(d, bx, by, i)
        img = img.crop(crop)
        if img.width > OUT_WIDTH:
            img = img.resize((OUT_WIDTH, round(img.height * OUT_WIDTH / img.width)), Image.LANCZOS)
        img.save(os.path.join(OUT, name), optimize=True)
        print("wrote", name, img.size)


if __name__ == "__main__":
    main()
