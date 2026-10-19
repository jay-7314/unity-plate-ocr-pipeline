"""번호판 OCR 서버
Unity가 보낸 번호판 이미지(JPEG)를 EasyOCR로 읽어서 글자를 돌려준다.

통신 규칙 (TCP, 포트 5005)
  요청: [4바이트 길이(빅엔디안)] + [JPEG 바이트]
  응답: [4바이트 길이(빅엔디안)] + [UTF-8 문자열]
        문자열이 비어 있으면 "읽지 못함"
"""
import re
import socket
import struct

import cv2
import easyocr
import numpy as np

HOST = "127.0.0.1"
PORT = 5005

# 한국 번호판 형식: 숫자 2~3개 + 한글 1개 + 숫자 4개 (예: 123가4567)
PLATE_RE = re.compile(r"\d{2,3}[가-힣]\d{4}")
# 한글을 못 읽었을 때 "?"로 표시한 형태도 허용 (예: 152?3108)
PARTIAL_RE = re.compile(r"\d{3}[가-힣?]\d{4}")
HANGUL = "가나다라마바사아자하거너더러머버서어저고노도로모보소오조구누두루무부수우주허호배"
DIGITS = "0123456789"

import torch
USE_GPU = torch.cuda.is_available()
print("GPU 사용:", USE_GPU)
reader = easyocr.Reader(["ko"], gpu=USE_GPU)


def normalize(raw):
    """읽은 문자열에서 번호판 형식을 찾아 (앞숫자, 한글, 뒤숫자)로 나눈다.
    숫자만 읽혔으면 한글 자리는 ?로 둔다 (지어내지 않는다)."""
    m = PLATE_RE.search(raw)
    if m:
        s = m.group()
        return s[:-5], s[-5], s[-4:]
    m = re.search(r"\d{7,8}", raw)
    if m:
        s = m.group()
        return s[:3], "?", s[-4:]
    return None


def ocr_in_order(im):
    """글자 덩어리를 왼쪽(x) 순서로 정렬해서 이어 붙인다.
    (EasyOCR가 순서를 뒤바꾸는 경우가 있고, 번호판 주변의 작은 글씨까지 읽는 경우가 있다.)
    번호판 글자는 주변 글씨보다 크므로, 가장 큰 덩어리의 60% 미만 높이는 버린다."""
    found = reader.readtext(im, allowlist=DIGITS + HANGUL, detail=1)
    if not found:
        return ""
    heights = [max(p[1] for p in r[0]) - min(p[1] for p in r[0]) for r in found]
    hmax = max(heights)
    keep = [r for r, h in zip(found, heights) if h >= 0.6 * hmax]
    keep.sort(key=lambda r: min(p[0] for p in r[0]))
    return "".join(r[1] for r in keep).replace(" ", "")


def read_plate(img_bgr):
    """번호판 이미지 -> 번호 문자열 (못 읽으면 빈 문자열)
    전처리 방식을 여러 가지 시도하되, 한글까지 읽히면 거기서 멈춘다.
    끝까지 한글이 안 읽히면 한글 자리를 ?로 둔다 (지어내지 않는다)."""
    gray = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    big = cv2.resize(gray, None, fx=4, fy=4, interpolation=cv2.INTER_CUBIC)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(4, 4)).apply(big)
    blur = cv2.GaussianBlur(big, (0, 0), 3)
    sharp = cv2.addWeighted(big, 1.8, blur, -0.8, 0)
    huge = cv2.resize(gray, None, fx=6, fy=6, interpolation=cv2.INTER_CUBIC)

    fallback = ""   # 한글은 못 읽었지만 숫자는 읽은 첫 결과
    for name, im in (("원본", big), ("대비보정", clahe), ("선명화", sharp), ("6배", huge)):
        raw = ocr_in_order(im)
        parsed = normalize(raw)
        print(f"  [{name}] 원문: {raw} -> {parsed if parsed else '(형식 안 맞음)'}")
        if not parsed:
            continue
        text = "".join(parsed)
        if parsed[1] != "?":
            return text            # 한글까지 읽힘
        if not fallback:
            fallback = text
    return fallback


def recv_exact(conn, n):
    buf = b""
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def handle(conn):
    while True:
        head = recv_exact(conn, 4)
        if head is None:
            return
        size = struct.unpack(">I", head)[0]
        data = recv_exact(conn, size)
        if data is None:
            return
        img = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
        if img is not None:
            cv2.imwrite("last_crop.jpg", img)  # 확인용: 마지막으로 받은 이미지 저장
        text = read_plate(img) if img is not None else ""
        print("읽은 결과:", text if text else "(읽지 못함)")
        out = text.encode("utf-8")
        conn.sendall(struct.pack(">I", len(out)) + out)


def main():
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((HOST, PORT))
    srv.listen(1)
    print(f"OCR 서버 대기 중 {HOST}:{PORT}")
    while True:
        conn, addr = srv.accept()
        print("Unity 연결:", addr)
        try:
            handle(conn)
        except (ConnectionError, OSError) as e:
            print("연결 오류:", e)
        finally:
            conn.close()
            print("연결 종료, 다시 대기")


if __name__ == "__main__":
    main()