"""realbasicvsr.py를 흉내 내는 가짜 스크립트 (통합 테스트 전용). torch 없이 ffmpeg로 4배 키우기만 한다.

진짜와 같은 인자를 받고, 같은 진행 줄("frame=i/N")과 끝 줄("done frames=N seconds=…")을 찍는다.
환경 변수 NEKOVUP_MOCK_RBVSR_FAIL=1 이면 아무것도 만들지 않고 오류로 끝낸다.
"""
import argparse
import os
import subprocess
import sys


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--ffmpeg', required=True)
    p.add_argument('--weights', required=True)
    p.add_argument('--input', required=True)
    p.add_argument('--output', required=True)
    p.add_argument('--width', type=int, required=True)
    p.add_argument('--height', type=int, required=True)
    p.add_argument('--frames', type=int, required=True)
    p.add_argument('--fps', required=True)
    p.add_argument('--crf', type=int, default=12)
    p.add_argument('--preset', default='veryfast')
    p.add_argument('--matrix', default=None)
    p.add_argument('--range', dest='color_range', default=None, choices=['tv', 'pc'])
    p.add_argument('--chunk', type=int, default=24)
    p.add_argument('--overlap', type=int, default=3)
    p.add_argument('--device', default='cuda')
    args = p.parse_args()

    if os.environ.get('NEKOVUP_MOCK_RBVSR_FAIL') == '1':
        print('Traceback (most recent call last): RuntimeError: CUDA out of memory (mock)', file=sys.stderr, flush=True)
        return 1
    if not os.path.isfile(args.weights):
        print(f'weights not found: {args.weights}', file=sys.stderr, flush=True)
        return 1

    print('warning: mock — 모델 대신 ffmpeg로 키웁니다', flush=True)
    print(f'frame={args.frames // 2}/{args.frames}', flush=True)
    result = subprocess.run([args.ffmpeg, '-hide_banner', '-nostdin', '-y', '-loglevel', 'error', '-i', args.input, '-map', '0:v:0',
                             # 진짜는 프레임을 날것으로 주고받아 픽셀 모양(SAR) 표기가 사라진다. 가짜도 똑같이 지운다.
                             '-vf', 'scale=iw*4:ih*4:flags=neighbor,setsar=1', '-c:v', 'libx264', '-crf', str(args.crf), '-preset', args.preset,
                             '-pix_fmt', 'yuv420p', '-bf', '0', args.output])
    if result.returncode != 0:
        return 3
    print(f'frame={args.frames}/{args.frames}', flush=True)
    print(f'done frames={args.frames} seconds=0.1', flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
