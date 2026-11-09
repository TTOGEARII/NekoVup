"""ProPainter의 inference_propainter.py를 흉내 내는 가짜 스크립트 (통합 테스트 전용).

GPU도 가중치도 없이, 진짜와 같은 인자를 받아 같은 폴더 구조로 결과를 낸다.
  - 입력: 프레임 폴더(-i), 마스크 PNG(-m), 출력 루트(-o)
  - 출력: <출력 루트>/<입력 폴더 이름>/frames/0000.png … (--save_frames 일 때만)
          + masked_in.mp4, inpaint_out.mp4 (내용 없는 자리 표시 파일)
  - --resize_ratio 를 주면 진짜처럼 "줄어든 크기"로 프레임을 저장한다.
  - 진짜는 가중치를 상대 경로 weights/ 에서 찾으므로, 작업 디렉터리가 이 스크립트 폴더가 아니면 실패한다.
  - 진짜는 프레임을 OpenCV로 읽고 쓰는데 Windows의 OpenCV는 영문이 아닌 글자가 든 경로를 열지 못한다.
    그래서 넘겨받은 경로(-i, -m, -o)에 영문 외 글자가 있으면 진짜처럼 실패한다.

환경 변수로 동작을 바꾼다:
  NEKOVUP_MOCK_MODE     identity(입력 그대로) | inpaint(마스크 안을 주변 색으로 메움, 기본)
  NEKOVUP_MOCK_LOG      호출될 때마다 입력 폴더 이름을 한 줄씩 덧붙일 파일
  NEKOVUP_MOCK_FAIL_AT  이 이름의 입력 폴더가 오면 오류로 끝낸다 (중단·재개 테스트)
  NEKOVUP_MOCK_CRASH_ONCE  이 파일이 없으면 만들고 프로세스가 죽은 것처럼 0xC0000409로 끝낸다 (재시도 테스트)
  NEKOVUP_MOCK_DELAY    프레임마다 쉬는 시간(초) (취소 테스트)
  NEKOVUP_MOCK_PIDFILE  자기 PID와, 일부러 띄운 자식 프로세스 PID를 적을 파일 (취소 테스트)
"""
import argparse
import os
import subprocess
import sys
import time

import numpy as np
from PIL import Image, ImageFilter


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument('-i', '--video', type=str, required=True)
    p.add_argument('-m', '--mask', type=str, required=True)
    p.add_argument('-o', '--output', type=str, default='results')
    p.add_argument('--resize_ratio', type=float, default=1.0)
    p.add_argument('--height', type=int, default=-1)
    p.add_argument('--width', type=int, default=-1)
    p.add_argument('--mask_dilation', type=int, default=4)
    p.add_argument('--ref_stride', type=int, default=10)
    p.add_argument('--neighbor_length', type=int, default=10)
    p.add_argument('--subvideo_length', type=int, default=80)
    p.add_argument('--raft_iter', type=int, default=20)
    p.add_argument('--mode', default='video_inpainting')
    p.add_argument('--save_fps', type=int, default=24)
    p.add_argument('--save_frames', action='store_true')
    p.add_argument('--fp16', action='store_true')
    return p.parse_args()


def dilate_cross(mask, iterations):
    """scipy.ndimage.binary_dilation 기본값(십자 모양)을 iterations번 적용한 것과 같다."""
    m = mask.copy()
    for _ in range(iterations):
        grown = m.copy()
        grown[1:, :] |= m[:-1, :]
        grown[:-1, :] |= m[1:, :]
        grown[:, 1:] |= m[:, :-1]
        grown[:, :-1] |= m[:, 1:]
        m = grown
    return m


def fill(frame, mask):
    """마스크 밖 픽셀만으로 흐리게 만든 값을 마스크 안에 채운다 (정규화 합성곱)."""
    keep = (~mask).astype(np.float32)
    weight = np.asarray(Image.fromarray((keep * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(14)), dtype=np.float32) / 255.0
    out = frame.astype(np.float32)
    for c in range(3):
        blurred = np.asarray(Image.fromarray((out[..., c] * keep).astype(np.uint8)).filter(ImageFilter.GaussianBlur(14)), dtype=np.float32)
        out[..., c] = np.where(mask, blurred / np.maximum(weight, 1e-3), out[..., c])
    return np.clip(out, 0, 255).astype(np.uint8)


def main():
    args = parse_args()
    here = os.path.dirname(os.path.abspath(__file__))
    if not os.path.samefile(os.getcwd(), here):
        print('가중치 폴더(weights/)를 찾을 수 없습니다: 작업 디렉터리가 스크립트 폴더가 아닙니다.', file=sys.stderr)
        return 3
    for given in (args.video, args.mask, args.output):
        if not given.isascii():
            print(f"[ WARN:0@0.1] global loadsave.cpp:278 cv::findDecoder imread_('{given}'): can't open/read file: check file path/integrity", file=sys.stderr)
            print('cv2.error: (-215:Assertion failed) !_src.empty() in function cv::cvtColor', file=sys.stderr)
            return 1
    if not os.path.isdir(args.video):
        print(f'프레임 폴더가 아닙니다: {args.video}', file=sys.stderr)
        return 2

    name = os.path.basename(os.path.normpath(args.video))
    log_path = os.environ.get('NEKOVUP_MOCK_LOG')
    if log_path:
        with open(log_path, 'a', encoding='utf-8') as f:
            f.write(name + '\n')
    if os.environ.get('NEKOVUP_MOCK_FAIL_AT') == name:
        print(f'일부러 낸 오류: {name}', file=sys.stderr)
        return 1

    crash_marker = os.environ.get('NEKOVUP_MOCK_CRASH_ONCE')
    if crash_marker and not os.path.exists(crash_marker):
        with open(crash_marker, 'w', encoding='utf-8') as f:
            f.write(name)
        os._exit(0xC0000409 - (1 << 32))  # os._exit은 부호 있는 32비트 값을 받는다

    pid_file = os.environ.get('NEKOVUP_MOCK_PIDFILE')
    if pid_file:
        child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])
        with open(pid_file, 'w', encoding='utf-8') as f:
            f.write(f'{os.getpid()}\n{child.pid}\n')

    mode = os.environ.get('NEKOVUP_MOCK_MODE', 'inpaint')
    delay = float(os.environ.get('NEKOVUP_MOCK_DELAY', '0'))

    files = sorted(os.listdir(args.video))
    frames = [Image.open(os.path.join(args.video, f)).convert('RGB') for f in files]
    size = frames[0].size
    if args.width != -1 and args.height != -1:
        size = (args.width, args.height)
    if args.resize_ratio != 1.0:
        size = (int(args.resize_ratio * size[0]), int(args.resize_ratio * size[1]))
    out_size = size
    process_size = (out_size[0] - out_size[0] % 8, out_size[1] - out_size[1] % 8)
    frames = [f.resize(process_size) if f.size != process_size else f for f in frames]

    mask_img = Image.open(args.mask).resize(process_size, Image.NEAREST).convert('L')
    mask = dilate_cross(np.asarray(mask_img) > 0, args.mask_dilation)

    save_root = os.path.join(args.output, name)
    os.makedirs(os.path.join(save_root, 'frames'), exist_ok=True)
    print(f'가짜 ProPainter: {name}, {len(frames)}프레임, 처리 크기 {process_size}, 모드 {mode}', flush=True)

    for idx, frame in enumerate(frames):
        if delay:
            time.sleep(delay)
        pixels = np.asarray(frame)
        result = pixels if mode == 'identity' else fill(pixels, mask)
        if args.save_frames:
            image = Image.fromarray(result)
            if image.size != out_size:
                image = image.resize(out_size, Image.BICUBIC)
            image.save(os.path.join(save_root, 'frames', str(idx).zfill(4) + '.png'), compress_level=1)

    for extra in ('masked_in.mp4', 'inpaint_out.mp4'):
        with open(os.path.join(save_root, extra), 'wb') as f:
            f.write(b'mock')
    print(f'\n모든 결과를 {save_root} 에 저장했습니다', flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
